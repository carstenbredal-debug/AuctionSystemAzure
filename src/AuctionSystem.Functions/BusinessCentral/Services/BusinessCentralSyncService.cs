using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Functions.BusinessCentral.Configuration;
using AuctionSystem.Functions.BusinessCentral.Models;
using AuctionSystem.Functions.Services;
using Microsoft.Extensions.Options;

namespace AuctionSystem.Functions.BusinessCentral.Services;

public class BusinessCentralSyncService
{
    // BC dimension systemIds (VENDORTYPE=BROKER/FARMER, CUSTOMERTYPE=BUYER) — per BC company, sourced
    // from BusinessCentralOptions (config) so a new company / TEST / PROD is a settings change.
    private readonly Guid VendorTypeDimensionId;
    private readonly Guid VendorTypeBrokerValueId;
    private readonly Guid VendorTypeFarmerValueId;
    private readonly Guid CustomerTypeDimensionId;
    private readonly Guid CustomerTypeBuyerValueId;

    private readonly BusinessCentralApiClient _bcClient;
    private readonly AuctionDbContext _db;
    private readonly BlobStorageService _blobStorage;
    private readonly ILogger<BusinessCentralSyncService> _logger;

    // Process-wide gate so only ONE BC document push runs at a time across the queue worker AND the
    // timer sweep. They run independently, and two concurrent pushes deadlock on BC's Sales Line table
    // (which then rolls a post back to Draft, etc.). batchSize:1 serializes the worker alone; this
    // extends serialization to cover the sweep too. (Single instance in DEV — PROD scale-out would need
    // a distributed lock; see the TEST/PROD runbook.)
    private static readonly SemaphoreSlim BcPushGate = new(1, 1);

    // SystemParameters keys for the (UI-editable) BC item numbers. There is intentionally NO hardcoded
    // fallback: an unset key resolves to empty and the push fails loudly (EnsureBcItemsExistAsync)
    // instead of silently posting a line to a stale/wrong item such as a leftover "BROKERCOMM".
    private const string LotSaleItemKey = "BcItem_LotSale";
    private const string AuctionFeeItemKey = "BcItem_AuctionFee";
    private const string CommissionItemKey = "BcItem_Commission";

    private readonly record struct BcItemNumbers(string LotSale, string AuctionFee, string Commission);

    // Resolve the BC item numbers from SystemParameters (admin-editable). Unset => empty (no fallback).
    private async Task<BcItemNumbers> GetBcItemNumbersAsync()
    {
        var keys = new[] { LotSaleItemKey, AuctionFeeItemKey, CommissionItemKey };
        var map = await _db.SystemParameters
            .Where(p => keys.Contains(p.Key))
            .ToDictionaryAsync(p => p.Key, p => p.Value);

        string Val(string key) =>
            map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : "";

        return new BcItemNumbers(Val(LotSaleItemKey), Val(AuctionFeeItemKey), Val(CommissionItemKey));
    }

    // Verify every BC item the document will use actually exists BEFORE any line is created — so an
    // invoice/credit note can never post with a missing or mis-configured item line (e.g. the broker
    // commission silently dropped or sent to a stale item). Throws a clear message that the push's
    // catch records as the BcSyncError shown on the "Push Failed" badge.
    private async Task EnsureBcItemsExistAsync(Guid companyId, BcItemNumbers items, bool needAuctionFee, bool needCommission)
    {
        var required = new List<(string Number, string Label, string ParamKey)> { (items.LotSale, "Lot sale", LotSaleItemKey) };
        if (needAuctionFee) required.Add((items.AuctionFee, "Auction fee", AuctionFeeItemKey));
        if (needCommission) required.Add((items.Commission, "Commission", CommissionItemKey));

        foreach (var (number, label, paramKey) in required)
        {
            if (string.IsNullOrWhiteSpace(number))
                throw new InvalidOperationException($"{label} BC item is not configured — set the {paramKey} parameter.");
            if (await _bcClient.GetItemByNumberAsync(companyId, number) == null)
                throw new InvalidOperationException($"{label} BC item '{number}' does not exist in Business Central — check the {paramKey} parameter.");
        }
    }

    public BusinessCentralSyncService(
        BusinessCentralApiClient bcClient,
        AuctionDbContext db,
        BlobStorageService blobStorage,
        ILogger<BusinessCentralSyncService> logger,
        IOptions<BusinessCentralOptions> options)
    {
        _bcClient = bcClient;
        _db = db;
        _blobStorage = blobStorage;
        _logger = logger;

        var o = options.Value;
        VendorTypeDimensionId = o.VendorTypeDimensionId;
        VendorTypeBrokerValueId = o.VendorTypeBrokerValueId;
        VendorTypeFarmerValueId = o.VendorTypeFarmerValueId;
        CustomerTypeDimensionId = o.CustomerTypeDimensionId;
        CustomerTypeBuyerValueId = o.CustomerTypeBuyerValueId;
    }

    /// <summary>
    /// Push all brokers to BC as vendors. Matches by BrokerNumber.
    /// </summary>
    public async Task<SyncResult> PushBrokersAsync()
    {
        var result = new SyncResult { Direction = "Push", EntityType = "Broker → BC Vendor" };

        var brokers = await _db.Set<Broker>()
            .Where(b => b.IsActive)
            .ToListAsync();

        result.TotalProcessed = brokers.Count;
        var companyId = await _bcClient.ResolveCompanyIdAsync();

        foreach (var broker in brokers)
        {
            try
            {
                var bcVendor = MapBrokerToVendor(broker);
                var existing = await _bcClient.GetVendorByNumberAsync(companyId, bcVendor.Number);

                if (existing is null)
                {
                    await _bcClient.CreateVendorAsync(companyId, bcVendor);
                    result.Created++;
                    _logger.LogInformation("Created BC vendor for broker {Number}", broker.BrokerNumber);
                }
                else
                {
                    bcVendor.Id = existing.Id;
                    bcVendor.ETag = existing.ETag;
                    await _bcClient.UpdateVendorAsync(companyId, bcVendor);
                    result.Updated++;
                    _logger.LogInformation("Updated BC vendor for broker {Number}", broker.BrokerNumber);
                }
                broker.BcSyncedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"Broker {broker.BrokerNumber}: {ex.Message}");
                _logger.LogError(ex, "Failed to sync broker {Number}", broker.BrokerNumber);
            }
        }

        await _db.SaveChangesAsync();
        return result;
    }

    /// <summary>
    /// Push all buyers to BC as customers. Matches by BuyerNumber.
    /// </summary>
    public async Task<SyncResult> PushBuyersAsync()
    {
        var result = new SyncResult { Direction = "Push", EntityType = "Buyer → BC Customer" };

        var buyers = await _db.Set<Buyer>()
            .Where(b => b.IsActive)
            .ToListAsync();

        result.TotalProcessed = buyers.Count;
        var companyId = await _bcClient.ResolveCompanyIdAsync();

        foreach (var buyer in buyers)
        {
            try
            {
                var existing = await _bcClient.GetCustomerByNumberAsync(companyId, buyer.BuyerNumber);
                var bcCustomer = MapBuyerToCustomer(buyer, existing);

                if (existing is null)
                {
                    await _bcClient.CreateCustomerAsync(companyId, bcCustomer);
                    result.Created++;
                    _logger.LogInformation("Created BC customer for buyer {Number}", buyer.BuyerNumber);
                }
                else
                {
                    bcCustomer.Id = existing.Id;
                    bcCustomer.ETag = existing.ETag;
                    await _bcClient.UpdateCustomerAsync(companyId, bcCustomer);
                    result.Updated++;
                    _logger.LogInformation("Updated BC customer for buyer {Number}", buyer.BuyerNumber);
                }
                buyer.BcSyncedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"Buyer {buyer.BuyerNumber}: {ex.Message}");
                _logger.LogError(ex, "Failed to sync buyer {Number}", buyer.BuyerNumber);
            }
        }

        await _db.SaveChangesAsync();
        return result;
    }

    /// <summary>
    /// Push invoices (non-credit-notes) to BC as Sales Invoices.
    /// Matches by InvoiceNumber as ExternalDocumentNumber.
    /// </summary>
    public async Task<SyncResult> PushInvoicesAsync()
    {
        var result = new SyncResult { Direction = "Push", EntityType = "Invoice → BC Sales Invoice" };

        // Only load not-yet-pushed invoices (mirrors PushCreditNotesAsync) instead of the whole
        // invoice table — keeps the push bounded as invoice history grows.
        var invoices = await _db.Set<Invoice>()
            .Include(i => i.Buyer)
            .Include(i => i.Broker)
            .Include(i => i.Lines)
            .Where(i => !i.IsCreditNote && (i.BcInvoiceNumber == null || i.BcInvoiceNumber == ""))
            .ToListAsync();

        result.TotalProcessed = invoices.Count;

        foreach (var invoice in invoices)
        {
            try
            {
                if (!string.IsNullOrEmpty(invoice.BcInvoiceNumber))
                {
                    result.Skipped++;
                    continue;
                }

                var outcome = await PushInvoiceToBcAsync(invoice);
                var docRef = string.IsNullOrEmpty(invoice.InvoiceNumber) ? $"AUC-{invoice.Id}" : invoice.InvoiceNumber;
                switch (outcome.Outcome)
                {
                    case BcPushOutcome.Posted:
                        result.Created++;
                        break;
                    case BcPushOutcome.AlreadyPushed:
                    case BcPushOutcome.Concurrent:
                        result.Skipped++;
                        break;
                    case BcPushOutcome.NotPushed:
                        result.Failed++;
                        result.Errors.Add($"Invoice {docRef}: {outcome.Reason}");
                        _logger.LogWarning("Invoice {DocRef} not pushed: {Reason}", docRef, outcome.Reason);
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"Invoice {invoice.InvoiceNumber}: {ex.Message}");
                _logger.LogError(ex, "Failed to sync invoice {Number}", invoice.InvoiceNumber);
            }
        }

        return result;
    }

    /// <summary>
    /// Push credit notes to BC as Sales Credit Memos.
    /// Only pushes credit notes that haven't been pushed yet (no BcInvoiceNumber).
    /// </summary>
    public async Task<SyncResult> PushCreditNotesAsync()
    {
        var result = new SyncResult { Direction = "Push", EntityType = "Credit Note → BC Sales Credit Memo" };

        var creditNotes = await _db.Set<Invoice>()
            .Include(i => i.Buyer)
            .Include(i => i.Broker)
            .Include(i => i.Lines)
            .Where(i => i.IsCreditNote && (i.BcInvoiceNumber == null || i.BcInvoiceNumber == ""))
            .ToListAsync();

        result.TotalProcessed = creditNotes.Count;

        foreach (var cn in creditNotes)
        {
            try
            {
                var outcome = await PushCreditNoteToBcAsync(cn);
                var docRef = string.IsNullOrEmpty(cn.InvoiceNumber) ? $"CN-{cn.Id}" : cn.InvoiceNumber;
                switch (outcome.Outcome)
                {
                    case BcPushOutcome.Posted:
                        result.Created++;
                        break;
                    case BcPushOutcome.AlreadyPushed:
                    case BcPushOutcome.Concurrent:
                        result.Skipped++;
                        break;
                    case BcPushOutcome.NotPushed:
                        result.Failed++;
                        result.Errors.Add($"Credit Note {docRef}: {outcome.Reason}");
                        _logger.LogWarning("Credit note {DocRef} not pushed: {Reason}", docRef, outcome.Reason);
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"Credit Note {cn.InvoiceNumber}: {ex.Message}");
                _logger.LogError(ex, "Failed to sync credit note {Number}", cn.InvoiceNumber);
            }
        }

        return result;
    }

    /// <summary>
    /// Push a single credit note to BC as a Sales Credit Memo.
    /// BC assigns the number from the credit memo number series.
    /// PDF is fetched from BC and stored in blob storage.
    /// </summary>
    public async Task<BcPushResult> PushCreditNoteToBcAsync(Invoice creditNote)
        => await WithPushLockAsync(() => PushCreditNoteToBcCoreAsync(creditNote));

    private async Task<BcPushResult> PushCreditNoteToBcCoreAsync(Invoice creditNote)
    {
        var companyId = await _bcClient.ResolveCompanyIdAsync();

        if (await SkipAlreadyPushedCreditNoteAsync(companyId, creditNote)) return BcPushResult.AlreadyPushed;

        if (!await TryClaimForBcPushAsync(creditNote.Id))
        {
            _logger.LogWarning("Credit note {Id} is already being pushed to BC (claim held); skipping this push", creditNote.Id);
            return BcPushResult.Concurrent;
        }

        try
        {
            var buyer = await ResolveBuyerAsBcCustomerAsync(companyId, creditNote.Id, creditNote.BuyerId, creditNote.Buyer);
            if (buyer == null)
            {
                // Did not post — free the claim so the next retry isn't skipped for 5 minutes.
                await ReleaseBcPushClaimAsync(creditNote.Id);
                var reason = $"Buyer {creditNote.Buyer?.BuyerNumber ?? creditNote.BuyerId.ToString()} is not a customer in BC";
                await RecordPushFailureAsync(creditNote.Id, reason);
                return BcPushResult.NotPushed(reason);
            }

            var extDocNumber = !string.IsNullOrEmpty(creditNote.InvoiceNumber) ? creditNote.InvoiceNumber : $"CN-{creditNote.Id}";
            var bcCreditMemo = new BcSalesCreditMemo
            {
                ExternalDocumentNumber = extDocNumber,
                CreditMemoDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                CustomerId = buyer.Value.BcCustomerId,
                CurrencyCode = "EUR"
            };

            await DeleteStaleDraftCreditMemoAsync(companyId, extDocNumber);
            var created = await _bcClient.CreateSalesCreditMemoAsync(companyId, bcCreditMemo);
            await AddCreditMemoLinesToBcAsync(companyId, created.Id, creditNote);

            var posted = await _bcClient.PostSalesCreditMemoAsync(companyId, created.Id, bcCreditMemo.ExternalDocumentNumber);

            // Posted, but the posted document couldn't be re-read. Do NOT fall back to the draft's
            // number — stamping a number that doesn't exist as a POSTED doc in BC creates the web/BC
            // desync (a number shown in the app that BC has no posted record for) and makes the sweep
            // skip it forever. Leave it unconfirmed; the next sweep's idempotency check finds the real
            // posted credit memo by external-doc number (CN-{Id}) and records the correct number.
            if (posted is null)
            {
                await ReleaseBcPushClaimAsync(creditNote.Id);
                var reason = "Posted to BC but the posted credit-memo number couldn't be confirmed; reconciling on the next sweep.";
                await RecordPushFailureAsync(creditNote.Id, reason);
                _logger.LogWarning("Credit note {Id}: {Reason}", creditNote.Id, reason);
                return BcPushResult.NotPushed(reason);
            }

            creditNote.BcInvoiceNumber = posted.Number;
            creditNote.BcInvoiceId = posted.Id;
            creditNote.InvoiceNumber = posted.Number;

            _logger.LogInformation("Posted credit memo {Number} (id {Id})", posted.Number, posted.Id);

            creditNote.BcSyncError = null;
            creditNote.BcSyncErrorAt = null;
            await TryFetchAndStoreCreditMemoPdfAsync(companyId, posted.Id, creditNote);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Created and posted BC sales credit memo {BcNumber} (customer={Customer})",
                creditNote.InvoiceNumber, buyer.Value.BuyerNumber);

            // Apply credit memo against original invoice in BC
            await TryApplyCreditMemoToInvoiceAsync(companyId, creditNote, buyer.Value.BuyerNumber);

            return BcPushResult.Posted;
        }
        catch (Exception ex)
        {
            // The push threw before the credit memo posted (BcInvoiceNumber not set). Release the
            // claim so the next retry can run instead of being skipped as "concurrent". If BC
            // actually posted but the local save failed, the idempotency check recovers it on retry.
            await ReleaseBcPushClaimAsync(creditNote.Id);
            await RecordPushFailureAsync(creditNote.Id, ex.Message);
            throw;
        }
    }

    // External-document keys the invoice push uses, in priority order. The stable AUC-{Id}
    // fallback is what makes idempotency work while InvoiceNumber is still "".
    private static IEnumerable<string> BcInvoiceExternalDocs(Invoice invoice)
    {
        if (!string.IsNullOrEmpty(invoice.InvoiceNumber)) yield return invoice.InvoiceNumber;
        yield return $"AUC-{invoice.Id}";
    }

    // Atomically claim an invoice/credit note for a BC push so two concurrent pushes of the same
    // document can't both create one in BC. Succeeds only if it isn't already pushed and isn't
    // currently claimed (a claim older than 5 minutes is treated as stale so a crashed push retries).
    private async Task<bool> TryClaimForBcPushAsync(int invoiceId)
    {
        var rows = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE auction.Invoices
            SET BcPushStartedAt = SYSUTCDATETIME()
            WHERE Id = {invoiceId}
              AND BcInvoiceNumber IS NULL
              AND (BcPushStartedAt IS NULL OR BcPushStartedAt < DATEADD(MINUTE, -5, SYSUTCDATETIME()))");
        return rows > 0;
    }

    // Release a BC-push claim taken by TryClaimForBcPushAsync when the push did NOT post the
    // document, so an immediate manual retry isn't blocked for 5 minutes as "concurrent".
    // Guarded on BcInvoiceNumber IS NULL so it can never wipe the lock on a doc that actually
    // posted (in which case BcInvoiceNumber, not the claim, is what guards against re-push).
    private async Task ReleaseBcPushClaimAsync(int invoiceId)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE auction.Invoices
            SET BcPushStartedAt = NULL
            WHERE Id = {invoiceId}
              AND BcInvoiceNumber IS NULL");
    }

    // Record why a push did not post so the admin UI can explain a stuck document without re-running
    // the sync. Isolated raw SQL (like the claim) so it can't persist partially-tracked entity state
    // left over from a failed push. Cleared on a successful post via the entity save in the push.
    private async Task RecordPushFailureAsync(int invoiceId, string reason)
    {
        var trimmed = string.IsNullOrEmpty(reason) ? "Unknown error"
            : reason.Length > 1000 ? reason[..1000] : reason;
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE auction.Invoices
            SET BcSyncError = {trimmed}, BcSyncErrorAt = SYSUTCDATETIME()
            WHERE Id = {invoiceId}");
    }

    // Delete a leftover draft (from a prior interrupted push) so we can recreate it cleanly.
    // Best-effort: failure here must not block the push.
    private async Task DeleteStaleDraftInvoiceAsync(Guid companyId, string extDocRef)
    {
        try
        {
            var draft = await _bcClient.GetSalesInvoiceByExternalDocAsync(companyId, extDocRef);
            if (draft is not null)
            {
                _logger.LogWarning("Deleting stale BC draft invoice {Id} (extDoc {Ext}) left by a prior interrupted push",
                    draft.Id, extDocRef);
                await _bcClient.DeleteSalesInvoiceAsync(companyId, draft.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up stale BC draft invoice for extDoc {Ext}", extDocRef);
        }
    }

    private async Task DeleteStaleDraftCreditMemoAsync(Guid companyId, string extDocRef)
    {
        try
        {
            var draft = await _bcClient.GetSalesCreditMemoByExternalDocAsync(companyId, extDocRef);
            if (draft is not null)
            {
                _logger.LogWarning("Deleting stale BC draft credit memo {Id} (extDoc {Ext}) left by a prior interrupted push",
                    draft.Id, extDocRef);
                await _bcClient.DeleteSalesCreditMemoAsync(companyId, draft.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up stale BC draft credit memo for extDoc {Ext}", extDocRef);
        }
    }

    private async Task<bool> SkipAlreadyPushedInvoiceAsync(Guid companyId, Invoice invoice)
    {
        if (!string.IsNullOrEmpty(invoice.BcInvoiceNumber))
        {
            _logger.LogInformation("Invoice {Id} already pushed to BC as {Number}", invoice.Id, invoice.BcInvoiceNumber);
            return true;
        }

        // Idempotency: look BC up by the SAME external-doc keys the push uses (the real number if
        // set, plus the stable AUC-{Id} fallback) against POSTED *and* draft invoices. A crash
        // between BC create/post and the local SaveChanges otherwise causes a duplicate on retry,
        // because the push posts with AUC-{Id} while InvoiceNumber is still "".
        foreach (var extDoc in BcInvoiceExternalDocs(invoice))
        {
            var posted = await _bcClient.GetPostedSalesInvoiceByExternalDocAsync(companyId, extDoc);
            if (posted is not null)
            {
                invoice.BcInvoiceNumber = posted.Number;
                invoice.BcInvoiceId = posted.Id;
                if (string.IsNullOrEmpty(invoice.InvoiceNumber)) invoice.InvoiceNumber = posted.Number;
                invoice.BcSyncError = null;
                invoice.BcSyncErrorAt = null;
                await _db.SaveChangesAsync();
                _logger.LogInformation("Invoice {Id} already POSTED in BC as {Number} (extDoc {Ext}); recording, not re-posting",
                    invoice.Id, posted.Number, extDoc);
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> BcCreditMemoExternalDocs(Invoice creditNote)
    {
        if (!string.IsNullOrEmpty(creditNote.InvoiceNumber)) yield return creditNote.InvoiceNumber;
        yield return $"CN-{creditNote.Id}";
    }

    private async Task<bool> SkipAlreadyPushedCreditNoteAsync(Guid companyId, Invoice creditNote)
    {
        if (!string.IsNullOrEmpty(creditNote.BcInvoiceNumber))
        {
            _logger.LogInformation("Credit note {Id} already pushed to BC as {Number}", creditNote.Id, creditNote.BcInvoiceNumber);
            return true;
        }

        // Idempotency by the same external-doc keys the push uses (real number if set, plus the
        // stable CN-{Id} fallback), against POSTED and draft credit memos — prevents a duplicate
        // on retry after a crash between BC create/post and the local SaveChanges.
        foreach (var extDoc in BcCreditMemoExternalDocs(creditNote))
        {
            var posted = await _bcClient.GetPostedSalesCreditMemoByExternalDocAsync(companyId, extDoc);
            if (posted is not null)
            {
                creditNote.BcInvoiceNumber = posted.Number;
                creditNote.BcInvoiceId = posted.Id;
                if (string.IsNullOrEmpty(creditNote.InvoiceNumber)) creditNote.InvoiceNumber = posted.Number;
                creditNote.BcSyncError = null;
                creditNote.BcSyncErrorAt = null;
                await _db.SaveChangesAsync();
                _logger.LogInformation("Credit note {Id} already POSTED in BC as {Number} (extDoc {Ext}); recording, not re-posting",
                    creditNote.Id, posted.Number, extDoc);
                return true;
            }
        }
        return false;
    }

    private readonly record struct ResolvedBuyer(Guid BcCustomerId, string BuyerNumber);

    private async Task<ResolvedBuyer?> ResolveBuyerAsBcCustomerAsync(Guid companyId, int documentId, int buyerId, Buyer? buyer)
    {
        buyer ??= await _db.Buyers.FindAsync(buyerId);
        if (buyer == null)
        {
            _logger.LogWarning("Document {Id}: Buyer {BuyerId} not found, skipping BC push", documentId, buyerId);
            return null;
        }

        var bcCustomer = await _bcClient.GetCustomerByNumberAsync(companyId, buyer.BuyerNumber);
        if (bcCustomer is null)
        {
            _logger.LogWarning("Document {Id}: Buyer {Number} not found in BC, skipping", documentId, buyer.BuyerNumber);
            return null;
        }

        return new ResolvedBuyer(bcCustomer.Id, buyer.BuyerNumber);
    }

    private async Task AddInvoiceLinesToBcAsync(Guid companyId, Guid documentId, Invoice invoice)
    {
        var items = await GetBcItemNumbersAsync();
        await EnsureBcItemsExistAsync(companyId, items, invoice.AuctionFee != 0, invoice.Commission != 0);
        int seq = 10000;
        foreach (var line in invoice.Lines)
        {
            var bcLine = new BcSalesInvoiceLine
            {
                DocumentId = documentId,
                Sequence = seq,
                LineType = "Item",
                LineObjectNumber = items.LotSale,
                Description = $"Lot {line.LotNumber}: {line.Description} ({line.Skins} skins)",
                Quantity = line.Skins,
                UnitPrice = line.PricePerSkin
            };
            await _bcClient.CreateSalesInvoiceLineAsync(companyId, documentId, bcLine);
            seq += 10000;
        }

        if (invoice.AuctionFee != 0)
        {
            await _bcClient.CreateSalesInvoiceLineAsync(companyId, documentId, new BcSalesInvoiceLine
            {
                DocumentId = documentId, Sequence = seq, LineType = "Item",
                LineObjectNumber = items.AuctionFee, Description = "Auction Fee", Quantity = 1, UnitPrice = invoice.AuctionFee
            });
            seq += 10000;
        }

        if (invoice.Commission != 0)
        {
            await _bcClient.CreateSalesInvoiceLineAsync(companyId, documentId, new BcSalesInvoiceLine
            {
                DocumentId = documentId, Sequence = seq, LineType = "Item",
                LineObjectNumber = items.Commission, Description = "Commission", Quantity = 1, UnitPrice = invoice.Commission
            });
        }
    }

    private async Task AddCreditMemoLinesToBcAsync(Guid companyId, Guid documentId, Invoice creditNote)
    {
        var items = await GetBcItemNumbersAsync();
        await EnsureBcItemsExistAsync(companyId, items, creditNote.AuctionFee != 0, creditNote.Commission != 0);
        int seq = 10000;
        foreach (var line in creditNote.Lines)
        {
            var bcLine = new BcSalesCreditMemoLine
            {
                DocumentId = documentId,
                Sequence = seq,
                LineType = "Item",
                LineObjectNumber = items.LotSale,
                Description = $"Lot {line.LotNumber}: {line.Description} ({Math.Abs(line.Skins)} skins)",
                Quantity = Math.Abs(line.Skins),
                UnitPrice = Math.Abs(line.PricePerSkin)
            };
            await _bcClient.CreateSalesCreditMemoLineAsync(companyId, documentId, bcLine);
            seq += 10000;
        }

        if (creditNote.AuctionFee != 0)
        {
            await _bcClient.CreateSalesCreditMemoLineAsync(companyId, documentId, new BcSalesCreditMemoLine
            {
                DocumentId = documentId, Sequence = seq, LineType = "Item",
                LineObjectNumber = items.AuctionFee, Description = "Auction Fee", Quantity = 1, UnitPrice = Math.Abs(creditNote.AuctionFee)
            });
            seq += 10000;
        }

        if (creditNote.Commission != 0)
        {
            await _bcClient.CreateSalesCreditMemoLineAsync(companyId, documentId, new BcSalesCreditMemoLine
            {
                DocumentId = documentId, Sequence = seq, LineType = "Item",
                LineObjectNumber = items.Commission, Description = "Commission", Quantity = 1, UnitPrice = Math.Abs(creditNote.Commission)
            });
        }
    }

    private async Task TryApplyCreditMemoToInvoiceAsync(Guid companyId, Invoice creditNote, string buyerNumber)
    {
        try
        {
            // Get original invoice's BC number
            var originalInvoice = creditNote.OriginalInvoice
                ?? (creditNote.OriginalInvoiceId.HasValue
                    ? await _db.Invoices.FindAsync(creditNote.OriginalInvoiceId.Value)
                    : null);

            if (originalInvoice == null || string.IsNullOrEmpty(originalInvoice.BcInvoiceNumber))
            {
                _logger.LogWarning("Credit note {Id}: cannot apply to invoice — original invoice not found or not pushed to BC", creditNote.Id);
                return;
            }

            // Find the credit memo entry in BC ledger
            var creditMemoEntries = await _bcClient.GetCustomerLedgerEntriesByCustomerAsync(
                companyId, buyerNumber, "Credit Memo", true);

            var creditMemoEntry = creditMemoEntries
                .FirstOrDefault(e => e.DocumentNo == creditNote.BcInvoiceNumber);

            if (creditMemoEntry == null)
            {
                _logger.LogWarning("Credit note {Id}: credit memo entry not found in BC ledger for {BcNumber}",
                    creditNote.Id, creditNote.BcInvoiceNumber);
                return;
            }

            // Apply the credit memo to the original invoice
            // BC extension handles posting date automatically (uses max of today, payment date, invoice date)
            var result = await _bcClient.ApplyCreditMemoToInvoiceAsync(
                companyId,
                buyerNumber,
                creditMemoEntry.EntryNo,
                originalInvoice.BcInvoiceNumber!);

            if (result.ResultStatus == "Error")
            {
                _logger.LogWarning("Credit note {Id}: BC application failed — {Msg}", creditNote.Id, result.ResultMessage);
            }
            else
            {
                _logger.LogInformation("Credit note {Id}: applied to invoice {InvNo} in BC — {Msg}",
                    creditNote.Id, originalInvoice.BcInvoiceNumber, result.ResultMessage);

                // Mark the credit note as alloted (applied in BC)
                creditNote.Status = Domain.Enums.InvoiceStatus.Alloted;

                // Update original invoice status based on credited lots
                var originalLotNumbers = await _db.Set<Domain.Entities.InvoiceLine>()
                    .Where(l => l.InvoiceId == originalInvoice.Id)
                    .Select(l => l.LotNumber)
                    .ToListAsync();

                var creditedLotNumbers = await _db.Set<Domain.Entities.InvoiceLine>()
                    .Where(l => l.Invoice.OriginalInvoiceId == originalInvoice.Id && l.Invoice.IsCreditNote)
                    .Select(l => l.LotNumber)
                    .Distinct()
                    .ToListAsync();

                if (originalLotNumbers.Count > 0 && creditedLotNumbers.Count >= originalLotNumbers.Count)
                    originalInvoice.Status = Domain.Enums.InvoiceStatus.FullyCredited;
                else if (creditedLotNumbers.Count > 0)
                    originalInvoice.Status = Domain.Enums.InvoiceStatus.PartiallyCredited;

                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Credit note {Id}: failed to apply credit memo in BC", creditNote.Id);
        }
    }

    /// <summary>
    /// Push a single invoice to BC as a Sales Invoice.
    /// BC assigns the invoice number from the SALESINV number series.
    /// PDF is fetched from BC and stored in blob storage.
    /// </summary>
    public async Task<BcPushResult> PushInvoiceToBcAsync(Invoice invoice)
        => await WithPushLockAsync(() => PushInvoiceToBcCoreAsync(invoice));

    // Serialize every BC document push across ALL function instances. The per-process SemaphoreSlim is
    // the cheap first gate; a SQL application lock (held on the shared DB) is the cross-instance gate —
    // under load the queue scales out and a per-process lock alone can't stop two instances pushing at
    // once, which deadlocks BC's Sales Line table (and leaves rolled-back/empty posts). The Session-
    // scoped applock is held on a dedicated connection for the whole push and released when it closes.
    private async Task<BcPushResult> WithPushLockAsync(Func<Task<BcPushResult>> push)
    {
        await BcPushGate.WaitAsync();
        var conn = new Microsoft.Data.SqlClient.SqlConnection(_db.Database.GetConnectionString());
        try
        {
            await conn.OpenAsync();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'BcDocumentPush', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 120000; SELECT @r;";
                cmd.CommandTimeout = 150;
                var rc = (int)(await cmd.ExecuteScalarAsync() ?? -999);
                if (rc < 0) _logger.LogWarning("BC push applock not acquired (rc={Rc}); proceeding without cross-instance lock", rc);
            }
            return await push();
        }
        finally
        {
            await conn.DisposeAsync(); // closing the connection releases the Session-scoped applock
            BcPushGate.Release();
        }
    }

    private async Task<BcPushResult> PushInvoiceToBcCoreAsync(Invoice invoice)
    {
        var companyId = await _bcClient.ResolveCompanyIdAsync();

        if (await SkipAlreadyPushedInvoiceAsync(companyId, invoice)) return BcPushResult.AlreadyPushed;

        if (!await TryClaimForBcPushAsync(invoice.Id))
        {
            _logger.LogWarning("Invoice {Id} is already being pushed to BC (claim held); skipping this push", invoice.Id);
            return BcPushResult.Concurrent;
        }

        try
        {
            var buyer = await ResolveBuyerAsBcCustomerAsync(companyId, invoice.Id, invoice.BuyerId, invoice.Buyer);
            if (buyer == null)
            {
                // Did not post — free the claim so the next retry isn't skipped for 5 minutes.
                await ReleaseBcPushClaimAsync(invoice.Id);
                var reason = $"Buyer {invoice.Buyer?.BuyerNumber ?? invoice.BuyerId.ToString()} is not a customer in BC";
                await RecordPushFailureAsync(invoice.Id, reason);
                return BcPushResult.NotPushed(reason);
            }

            var extDocRef = !string.IsNullOrEmpty(invoice.InvoiceNumber) ? invoice.InvoiceNumber : $"AUC-{invoice.Id}";
            var postingDate = DateTime.UtcNow;
            var bcInvoice = new BcSalesInvoice
            {
                ExternalDocumentNumber = extDocRef,
                InvoiceDate = postingDate.ToString("yyyy-MM-dd"),
                DueDate = (invoice.PromptDate ?? postingDate.AddDays(30)).ToString("yyyy-MM-dd"),
                CustomerId = buyer.Value.BcCustomerId,
                CurrencyCode = "EUR"
            };

            // Remove any stale draft from a prior interrupted push (half-built lines / never posted)
            // before creating a fresh one. It isn't posted — the idempotency check ran first — so it's
            // safe to delete, and this avoids orphaned drafts and partial-line leftovers.
            await DeleteStaleDraftInvoiceAsync(companyId, extDocRef);
            var created = await _bcClient.CreateSalesInvoiceAsync(companyId, bcInvoice);
            await AddInvoiceLinesToBcAsync(companyId, created.Id, invoice);

            var posted = await _bcClient.PostSalesInvoiceAsync(companyId, created.Id, bcInvoice.ExternalDocumentNumber);

            // Posted, but the posted document couldn't be re-read. Do NOT fall back to the draft's
            // number — that desyncs web vs BC and makes the sweep skip it forever. Leave it unconfirmed;
            // the next sweep's idempotency check finds the real posted invoice by external-doc number
            // (AUC-{Id}) and records the correct number.
            if (posted is null)
            {
                await ReleaseBcPushClaimAsync(invoice.Id);
                var reason = "Posted to BC but the posted invoice number couldn't be confirmed; reconciling on the next sweep.";
                await RecordPushFailureAsync(invoice.Id, reason);
                _logger.LogWarning("Invoice {Id}: {Reason}", invoice.Id, reason);
                return BcPushResult.NotPushed(reason);
            }

            invoice.BcInvoiceNumber = posted.Number;
            invoice.BcInvoiceId = posted.Id;
            invoice.InvoiceNumber = posted.Number;

            _logger.LogInformation("Posted invoice {Number} (id {Id})", posted.Number, posted.Id);

            invoice.BcSyncError = null;
            invoice.BcSyncErrorAt = null;
            await TryFetchAndStorePdfAsync(companyId, posted.Id, invoice);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Created and posted BC sales invoice {BcNumber} (customer={Customer})",
                invoice.InvoiceNumber, buyer.Value.BuyerNumber);

            return BcPushResult.Posted;
        }
        catch (Exception ex)
        {
            // The push threw before the invoice posted (BcInvoiceNumber not set). Release the claim
            // so the next retry can run instead of being skipped as "concurrent". If BC actually
            // posted but the local save failed, the idempotency check recovers it on retry.
            await ReleaseBcPushClaimAsync(invoice.Id);
            await RecordPushFailureAsync(invoice.Id, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Pull customers from BC and return them (for display/reconciliation).
    /// </summary>
    public async Task<List<BcCustomer>> PullCustomersAsync()
    {
        var companyId = await _bcClient.ResolveCompanyIdAsync();
        return await _bcClient.GetCustomersAsync(companyId);
    }

    /// <summary>
    /// Pull items from BC.
    /// </summary>
    public async Task<List<BcItem>> PullItemsAsync()
    {
        var companyId = await _bcClient.ResolveCompanyIdAsync();
        return await _bcClient.GetItemsAsync(companyId);
    }

    /// <summary>
    /// Push all farmers to BC as vendors. Matches by FarmerNumber.
    /// </summary>
    public async Task<SyncResult> PushFarmersAsync()
    {
        var result = new SyncResult { Direction = "Push", EntityType = "Farmer → BC Vendor" };

        var farmers = await _db.Set<Farmer>()
            .Where(f => f.IsActive)
            .ToListAsync();

        result.TotalProcessed = farmers.Count;
        var companyId = await _bcClient.ResolveCompanyIdAsync();

        foreach (var farmer in farmers)
        {
            try
            {
                var bcVendor = MapFarmerToVendor(farmer);
                var existing = await _bcClient.GetVendorByNumberAsync(companyId, bcVendor.Number);

                if (existing is null)
                {
                    await _bcClient.CreateVendorAsync(companyId, bcVendor);
                    result.Created++;
                    _logger.LogInformation("Created BC vendor for farmer {Number}", farmer.FarmerNumber);
                }
                else
                {
                    bcVendor.Id = existing.Id;
                    bcVendor.ETag = existing.ETag;
                    await _bcClient.UpdateVendorAsync(companyId, bcVendor);
                    result.Updated++;
                    _logger.LogInformation("Updated BC vendor for farmer {Number}", farmer.FarmerNumber);
                }
                farmer.BcSyncedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"Farmer {farmer.FarmerNumber}: {ex.Message}");
                _logger.LogError(ex, "Failed to sync farmer {Number}", farmer.FarmerNumber);
            }
        }

        await _db.SaveChangesAsync();
        return result;
    }

    /// <summary>
    /// Full sync: push brokers, buyers, farmers, invoices, credit notes.
    public async Task<Guid> GetCompanyIdAsync() => await _bcClient.ResolveCompanyIdAsync();

    public async Task RefreshPdfAsync(Guid companyId, Invoice invoice)
    {
        if (invoice.BcInvoiceId == null) return;
        await TryFetchAndStorePdfAsync(companyId, invoice.BcInvoiceId.Value, invoice);
    }

    /// </summary>
    public async Task<List<SyncResult>> RunFullSyncAsync()
    {
        var results = new List<SyncResult>();

        _logger.LogInformation("Starting full BC sync...");

        results.Add(await PushBrokersAsync());
        results.Add(await PushFarmersAsync());
        results.Add(await PushBuyersAsync());
        results.Add(await PushInvoicesAsync());
        results.Add(await PushCreditNotesAsync());

        _logger.LogInformation("Full BC sync complete");
        return results;
    }

    /// <summary>
    /// Get sync status: how many entities are synced vs unsynced.
    /// </summary>
    public async Task<object> GetSyncStatusAsync()
    {
        var brokers = await _db.Set<Broker>().Where(b => b.IsActive).ToListAsync();
        var buyers = await _db.Set<Buyer>().Where(b => b.IsActive).ToListAsync();
        var farmers = await _db.Set<Farmer>().Where(f => f.IsActive).ToListAsync();
        var invoices = await _db.Set<Invoice>().Where(i => !i.IsCreditNote).CountAsync();
        var creditNotes = await _db.Set<Invoice>().Where(i => i.IsCreditNote).CountAsync();

        var brokersSynced = brokers.Count(b => b.BcSyncedAt.HasValue);
        var buyersSynced = buyers.Count(b => b.BcSyncedAt.HasValue);
        var farmersSynced = farmers.Count(f => f.BcSyncedAt.HasValue);

        return new
        {
            Brokers = new
            {
                Total = brokers.Count,
                Synced = brokersSynced,
                Unsynced = brokers.Count - brokersSynced
            },
            Buyers = new
            {
                Total = buyers.Count,
                Synced = buyersSynced,
                Unsynced = buyers.Count - buyersSynced
            },
            Farmers = new
            {
                Total = farmers.Count,
                Synced = farmersSynced,
                Unsynced = farmers.Count - farmersSynced
            },
            Invoices = invoices,
            CreditNotes = creditNotes
        };
    }

    /// <summary>
    /// Reconcile the local BcSyncedAt flags against BC reality: mark an entity synced if its number
    /// exists in BC, clear it if it doesn't. Keeps the sync-status counts accurate even if data
    /// drifts (e.g. a vendor/customer deleted directly in BC). Bulk-pulls vendors/customers once
    /// rather than calling BC per entity. Returns the number of flags corrected.
    /// </summary>
    public async Task<int> ReconcileSyncStatusAsync()
    {
        var companyId = await _bcClient.ResolveCompanyIdAsync();

        var vendorNumbers = (await _bcClient.GetVendorsAsync(companyId))
            .Select(v => v.Number).Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customerNumbers = (await _bcClient.GetCustomersAsync(companyId))
            .Select(c => c.Number).Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changed = 0;

        static bool Apply(bool inBc, ref DateTime? flag)
        {
            if (inBc && flag == null) { flag = DateTime.UtcNow; return true; }
            if (!inBc && flag != null) { flag = null; return true; }
            return false;
        }

        foreach (var b in await _db.Set<Broker>().Where(b => b.IsActive).ToListAsync())
        {
            var f = b.BcSyncedAt;
            if (Apply(vendorNumbers.Contains(b.BrokerNumber), ref f)) { b.BcSyncedAt = f; changed++; }
        }
        foreach (var fa in await _db.Set<Farmer>().Where(f => f.IsActive).ToListAsync())
        {
            var f = fa.BcSyncedAt;
            if (Apply(vendorNumbers.Contains(fa.FarmerNumber), ref f)) { fa.BcSyncedAt = f; changed++; }
        }
        foreach (var bu in await _db.Set<Buyer>().Where(b => b.IsActive).ToListAsync())
        {
            var f = bu.BcSyncedAt;
            if (Apply(customerNumbers.Contains(bu.BuyerNumber), ref f)) { bu.BcSyncedAt = f; changed++; }
        }

        if (changed > 0) await _db.SaveChangesAsync();
        _logger.LogInformation("BC sync-status reconcile: corrected {Changed} flag(s) ({Vendors} BC vendors, {Customers} BC customers)",
            changed, vendorNumbers.Count, customerNumbers.Count);
        return changed;
    }

    // ── Single-entity push (called on create/update) ─────────

    public async Task PushSingleBrokerAsync(Broker broker)
    {
        var companyId = await _bcClient.ResolveCompanyIdAsync();
        var bcVendor = MapBrokerToVendor(broker);
        var existing = await _bcClient.GetVendorByNumberAsync(companyId, bcVendor.Number);

        BcVendor result;
        if (existing is null)
        {
            result = await _bcClient.CreateVendorAsync(companyId, bcVendor);
            _logger.LogInformation("Created BC vendor for broker {Number}", broker.BrokerNumber);
        }
        else
        {
            bcVendor.Id = existing.Id;
            bcVendor.ETag = existing.ETag;
            result = await _bcClient.UpdateVendorAsync(companyId, bcVendor);
            _logger.LogInformation("Updated BC vendor for broker {Number}", broker.BrokerNumber);
        }
        await PatchVendorVatRegAsync(companyId, result, broker.VatRegistrationNo);
        await SetDefaultDimensionSafeAsync(companyId, result.Id, VendorTypeDimensionId, VendorTypeBrokerValueId, "VENDORTYPE=BROKER", broker.BrokerNumber);
        broker.BcSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task PushSingleBuyerAsync(Buyer buyer)
    {
        var companyId = await _bcClient.ResolveCompanyIdAsync();
        var existing = await _bcClient.GetCustomerByNumberAsync(companyId, buyer.BuyerNumber);
        var bcCustomer = MapBuyerToCustomer(buyer, existing);

        BcCustomer result;
        if (existing is null)
        {
            result = await _bcClient.CreateCustomerAsync(companyId, bcCustomer);
            _logger.LogInformation("Created BC customer for buyer {Number}", buyer.BuyerNumber);
        }
        else
        {
            bcCustomer.Id = existing.Id;
            bcCustomer.ETag = existing.ETag;
            result = await _bcClient.UpdateCustomerAsync(companyId, bcCustomer);
            _logger.LogInformation("Updated BC customer for buyer {Number}", buyer.BuyerNumber);
        }
        await SetDefaultDimensionSafeAsync(companyId, result.Id, CustomerTypeDimensionId, CustomerTypeBuyerValueId, "CUSTOMERTYPE=BUYER", buyer.BuyerNumber);
        buyer.BcSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task PushSingleFarmerAsync(Farmer farmer)
    {
        var companyId = await _bcClient.ResolveCompanyIdAsync();
        var bcVendor = MapFarmerToVendor(farmer);
        var existing = await _bcClient.GetVendorByNumberAsync(companyId, bcVendor.Number);

        BcVendor result;
        if (existing is null)
        {
            result = await _bcClient.CreateVendorAsync(companyId, bcVendor);
            _logger.LogInformation("Created BC vendor for farmer {Number}", farmer.FarmerNumber);
        }
        else
        {
            bcVendor.Id = existing.Id;
            bcVendor.ETag = existing.ETag;
            result = await _bcClient.UpdateVendorAsync(companyId, bcVendor);
            _logger.LogInformation("Updated BC vendor for farmer {Number}", farmer.FarmerNumber);
        }
        await PatchVendorVatRegAsync(companyId, result, farmer.VatRegistrationNo);
        await SetDefaultDimensionSafeAsync(companyId, result.Id, VendorTypeDimensionId, VendorTypeFarmerValueId, "VENDORTYPE=FARMER", farmer.FarmerNumber);
        farmer.BcSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task SetDefaultDimensionSafeAsync(Guid companyId, Guid parentId, Guid dimensionId, Guid dimensionValueId, string label, string entityNumber)
    {
        try
        {
            await _bcClient.SetDefaultDimensionAsync(companyId, parentId, dimensionId, dimensionValueId);
            _logger.LogInformation("Set default dimension {Label} for {Number}", label, entityNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set default dimension {Label} for {Number}", label, entityNumber);
        }
    }

    private async Task PatchVendorVatRegAsync(Guid companyId, BcVendor vendor, string? vatRegNo)
    {
        if (string.IsNullOrEmpty(vatRegNo)) return;
        try
        {
            // Use standard v2.0 API which exposes taxRegistrationNumber
            var stdVendor = await _bcClient.GetStandardVendorByIdAsync(companyId, vendor.Id);
            if (stdVendor != null)
                await _bcClient.PatchVendorTaxRegistrationAsync(companyId, vendor.Id, vatRegNo, stdVendor.ETag!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to patch VAT registration for vendor {Number}", vendor.Number);
        }
    }

    // ── Mapping helpers ────────────────────────────────────────

    private static string Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maxLength ? value : value[..maxLength];

    private static BcVendor MapBrokerToVendor(Broker broker)
    {
        return new BcVendor
        {
            Number = broker.BrokerNumber,
            DisplayName = broker.CompanyName,
            AddressLine1 = Truncate(broker.AddressLine1, 50),
            AddressLine2 = Truncate(broker.AddressLine2, 50),
            City = Truncate(broker.City, 30),
            Country = broker.Country,
            PostalCode = Truncate(broker.PostalCode, 20),
            PhoneNumber = Truncate(broker.ContactPhone, 30),
            Email = Truncate(broker.ContactEmail, 80),
            Website = Truncate(broker.HomePage, 80),
            CurrencyCode = broker.Currency == "EUR" ? "EUR" : broker.Currency,
            Blocked = string.IsNullOrEmpty(broker.Blocked) ? "_x0020_" : broker.Blocked,
            GenBusPostingGroup = broker.GenBusPostingGroup,
            VatBusPostingGroup = broker.VatBusPostingGroup,
            VendorPostingGroup = broker.CustomerPostingGroup,
            PaymentTermsCode = broker.PaymentTerm,
            PaymentMethodCode = broker.PaymentMethod,
            VatRegistrationNo = broker.VatRegistrationNo
        };
    }

    private static BcCustomer MapBuyerToCustomer(Buyer buyer, BcCustomer? existing = null)
    {
        return new BcCustomer
        {
            Number = buyer.BuyerNumber,
            DisplayName = buyer.Name,
            Type = "Company",
            AddressLine1 = Truncate(buyer.AddressLine1, 50),
            AddressLine2 = Truncate(buyer.AddressLine2, 50),
            City = Truncate(buyer.City, 30),
            Country = buyer.Country,
            PostalCode = Truncate(buyer.PostalCode, 20),
            PhoneNumber = Truncate(buyer.ContactPhone, 30),
            Email = Truncate(buyer.ContactEmail, 80),
            Website = Truncate(buyer.HomePage, 80),
            // Send the currency explicitly (like brokers/farmers); previously EUR was blanked,
            // so EUR buyers showed no currency code in BC.
            CurrencyCode = buyer.Currency == "EUR" ? "EUR" : buyer.Currency,
            CreditLimit = buyer.CreditLimit,
            Blocked = string.IsNullOrEmpty(buyer.Blocked) ? "_x0020_" : buyer.Blocked,
            GenBusPostingGroup = !string.IsNullOrEmpty(buyer.GenBusPostingGroup) ? buyer.GenBusPostingGroup : existing?.GenBusPostingGroup ?? string.Empty,
            VatBusPostingGroup = !string.IsNullOrEmpty(buyer.VatBusPostingGroup) ? buyer.VatBusPostingGroup : existing?.VatBusPostingGroup ?? string.Empty,
            CustomerPostingGroup = !string.IsNullOrEmpty(buyer.CustomerPostingGroup) ? buyer.CustomerPostingGroup : existing?.CustomerPostingGroup ?? string.Empty,
            PaymentTermsCode = !string.IsNullOrEmpty(buyer.PaymentTerm) ? buyer.PaymentTerm : existing?.PaymentTermsCode ?? string.Empty,
            PaymentMethodCode = !string.IsNullOrEmpty(buyer.PaymentMethod) ? buyer.PaymentMethod : existing?.PaymentMethodCode ?? string.Empty,
            VatRegistrationNo = buyer.VatRegistrationNo
        };
    }

    private static BcVendor MapFarmerToVendor(Farmer farmer)
    {
        return new BcVendor
        {
            Number = farmer.FarmerNumber,
            DisplayName = farmer.Name,
            AddressLine1 = Truncate(farmer.AddressLine1, 50),
            AddressLine2 = Truncate(farmer.AddressLine2, 50),
            City = Truncate(farmer.City, 30),
            Country = farmer.Country,
            PostalCode = Truncate(farmer.PostalCode, 20),
            PhoneNumber = Truncate(farmer.ContactPhone, 30),
            Email = Truncate(farmer.ContactEmail, 80),
            Website = Truncate(farmer.HomePage, 80),
            CurrencyCode = farmer.Currency == "EUR" ? "EUR" : farmer.Currency,
            Blocked = string.IsNullOrEmpty(farmer.Blocked) ? "_x0020_" : farmer.Blocked,
            GenBusPostingGroup = farmer.GenBusPostingGroup,
            VatBusPostingGroup = farmer.VatBusPostingGroup,
            VendorPostingGroup = farmer.VendorPostingGroup,
            PaymentTermsCode = farmer.PaymentTerm,
            PaymentMethodCode = farmer.PaymentMethod,
            VatRegistrationNo = farmer.VatRegistrationNo
        };
    }

    private async Task TryFetchAndStorePdfAsync(Guid companyId, Guid bcInvoiceId, Invoice invoice)
    {
        try
        {
            // Strategy 1: Standard BC pdfDocument endpoint
            var pdfBytes = await _bcClient.GetSalesInvoicePdfAsync(companyId, bcInvoiceId);
            if (pdfBytes is { Length: > 0 })
            {
                var fileName = $"bc-{invoice.InvoiceNumber}.pdf";
                invoice.PdfUrl = await _blobStorage.UploadPdfAsync(fileName, pdfBytes);
                _logger.LogInformation("Stored BC invoice PDF via standard endpoint for {Number} ({Bytes} bytes)", invoice.InvoiceNumber, pdfBytes.Length);
                return;
            }
            _logger.LogWarning("Standard PDF endpoint returned empty for {Number}, trying custom endpoint", invoice.InvoiceNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Standard PDF endpoint failed for {Number}, trying custom endpoint", invoice.InvoiceNumber);
        }

        // Strategy 2: Custom BC extension endpoint (generates PDF and attaches to invoice)
        await TryCustomPdfGenerationAsync(companyId, bcInvoiceId, invoice, "Sales Invoice", "salesInvoices");
    }

    private async Task TryFetchAndStoreCreditMemoPdfAsync(Guid companyId, Guid bcCreditMemoId, Invoice creditNote)
    {
        try
        {
            var pdfBytes = await _bcClient.GetSalesCreditMemoPdfAsync(companyId, bcCreditMemoId);
            if (pdfBytes is { Length: > 0 })
            {
                var fileName = $"bc-{creditNote.InvoiceNumber}.pdf";
                creditNote.PdfUrl = await _blobStorage.UploadPdfAsync(fileName, pdfBytes);
                _logger.LogInformation("Stored BC credit memo PDF via standard endpoint for {Number} ({Bytes} bytes)", creditNote.InvoiceNumber, pdfBytes.Length);
                return;
            }
            _logger.LogWarning("Standard PDF endpoint returned empty for credit memo {Number}, trying custom endpoint", creditNote.InvoiceNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Standard PDF endpoint failed for credit memo {Number}, trying custom endpoint", creditNote.InvoiceNumber);
        }

        await TryCustomPdfGenerationAsync(companyId, bcCreditMemoId, creditNote, "Credit Memo", "salesCreditMemos");
    }

    private async Task TryCustomPdfGenerationAsync(Guid companyId, Guid bcDocId, Invoice invoice, string documentType, string entityType)
    {
        try
        {
            if (string.IsNullOrEmpty(invoice.InvoiceNumber))
            {
                _logger.LogWarning("No invoice number for custom PDF generation");
                return;
            }

            var (success, error, reportId) = await _bcClient.RequestPdfGenerationAsync(companyId, invoice.InvoiceNumber, documentType);
            _logger.LogInformation("Custom PDF generation result: success={Success}, reportId={ReportId}, error={Error}",
                success, reportId, error ?? "none");

            if (!success)
            {
                _logger.LogWarning("Custom PDF generation failed for {Number}: {Error}", invoice.InvoiceNumber, error);
                return;
            }

            // Wait a moment for BC to process the attachment
            await Task.Delay(2000);

            // Download the attached PDF
            var pdfBytes = await _bcClient.GetDocumentAttachmentPdfAsync(companyId, bcDocId, entityType);
            if (pdfBytes is { Length: > 0 })
            {
                var fileName = $"bc-{invoice.InvoiceNumber}.pdf";
                invoice.PdfUrl = await _blobStorage.UploadPdfAsync(fileName, pdfBytes);
                _logger.LogInformation("Stored PDF via custom endpoint for {Number} ({Bytes} bytes, report {ReportId})",
                    invoice.InvoiceNumber, pdfBytes.Length, reportId);
            }
            else
            {
                _logger.LogWarning("Custom endpoint reported success but no attachment found for {Number}", invoice.InvoiceNumber);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Custom PDF generation failed for {Number}", invoice.InvoiceNumber);
        }
    }
}
