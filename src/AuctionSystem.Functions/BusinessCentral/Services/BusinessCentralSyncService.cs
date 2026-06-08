using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Functions.BusinessCentral.Models;
using AuctionSystem.Functions.Services;

namespace AuctionSystem.Functions.BusinessCentral.Services;

public class BusinessCentralSyncService
{
    // BC dimension IDs for Lot Test 3
    private static readonly Guid VendorTypeDimensionId = Guid.Parse("0288ef73-a554-f111-a820-7c1e5271a821");
    private static readonly Guid VendorTypeBrokerValueId = Guid.Parse("728c715d-be54-f111-a820-7c1e5271a821");
    private static readonly Guid VendorTypeFarmerValueId = Guid.Parse("0688ef73-a554-f111-a820-7c1e5271a821");
    private static readonly Guid CustomerTypeDimensionId = Guid.Parse("0188ef73-a554-f111-a820-7c1e5271a821");
    private static readonly Guid CustomerTypeBuyerValueId = Guid.Parse("a807e197-f55a-f111-a820-70a8a55fc40b");

    private readonly BusinessCentralApiClient _bcClient;
    private readonly AuctionDbContext _db;
    private readonly BlobStorageService _blobStorage;
    private readonly ILogger<BusinessCentralSyncService> _logger;

    private readonly Dictionary<string, string> _itemNumberCache = new();

    public BusinessCentralSyncService(
        BusinessCentralApiClient bcClient,
        AuctionDbContext db,
        BlobStorageService blobStorage,
        ILogger<BusinessCentralSyncService> logger)
    {
        _bcClient = bcClient;
        _db = db;
        _blobStorage = blobStorage;
        _logger = logger;
    }

    private async Task<string> ResolveItemNumberAsync(Guid companyId, string displayName)
    {
        if (_itemNumberCache.TryGetValue(displayName, out var cached))
            return cached;

        var item = await _bcClient.GetItemByDisplayNameAsync(companyId, displayName);
        if (item == null)
            throw new InvalidOperationException($"BC service item with displayName '{displayName}' not found. Please create it in Business Central.");

        _logger.LogInformation("Resolved BC item '{DisplayName}' → number '{Number}'", displayName, item.Number);
        _itemNumberCache[displayName] = item.Number;
        return item.Number;
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

        var invoices = await _db.Set<Invoice>()
            .Include(i => i.Buyer)
            .Include(i => i.Broker)
            .Include(i => i.Lines)
            .Where(i => !i.IsCreditNote)
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

                await PushInvoiceToBcAsync(invoice);
                result.Created++;
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
                await PushCreditNoteToBcAsync(cn);
                result.Created++;
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
    public async Task PushCreditNoteToBcAsync(Invoice creditNote)
    {
        var companyId = await _bcClient.ResolveCompanyIdAsync();

        if (await SkipAlreadyPushedCreditNoteAsync(companyId, creditNote)) return;

        var buyer = await ResolveBuyerAsBcCustomerAsync(companyId, creditNote.Id, creditNote.BuyerId, creditNote.Buyer);
        if (buyer == null) return;

        var extDocNumber = !string.IsNullOrEmpty(creditNote.InvoiceNumber) ? creditNote.InvoiceNumber : $"CN-{creditNote.Id}";
        var bcCreditMemo = new BcSalesCreditMemo
        {
            ExternalDocumentNumber = extDocNumber,
            CreditMemoDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            CustomerId = buyer.Value.BcCustomerId,
            CurrencyCode = ""
        };

        var created = await _bcClient.CreateSalesCreditMemoAsync(companyId, bcCreditMemo);
        await AddCreditMemoLinesToBcAsync(companyId, created.Id, creditNote);

        var posted = await _bcClient.PostSalesCreditMemoAsync(companyId, created.Id, bcCreditMemo.ExternalDocumentNumber);
        var finalNumber = posted?.Number ?? created.Number;
        var finalId = posted?.Id ?? created.Id;

        creditNote.BcInvoiceNumber = finalNumber;
        creditNote.BcInvoiceId = finalId;
        creditNote.InvoiceNumber = finalNumber;

        _logger.LogInformation("Posted credit memo: finalNumber={FinalNumber}, finalId={FinalId}, postedWasNull={PostedNull}",
            finalNumber, finalId, posted == null);

        await TryFetchAndStoreCreditMemoPdfAsync(companyId, finalId, creditNote);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created and posted BC sales credit memo {BcNumber} (customer={Customer})",
            creditNote.InvoiceNumber, buyer.Value.BuyerNumber);

        // Apply credit memo against original invoice in BC
        await TryApplyCreditMemoToInvoiceAsync(companyId, creditNote, buyer.Value.BuyerNumber);
    }

    private async Task<bool> SkipAlreadyPushedInvoiceAsync(Guid companyId, Invoice invoice)
    {
        if (!string.IsNullOrEmpty(invoice.BcInvoiceNumber))
        {
            _logger.LogInformation("Invoice {Id} already pushed to BC as {Number}", invoice.Id, invoice.BcInvoiceNumber);
            return true;
        }

        if (!string.IsNullOrEmpty(invoice.InvoiceNumber))
        {
            var existing = await _bcClient.GetSalesInvoiceByExternalDocAsync(companyId, invoice.InvoiceNumber);
            if (existing is not null)
            {
                invoice.BcInvoiceNumber = existing.Number;
                invoice.BcInvoiceId = existing.Id;
                await _db.SaveChangesAsync();
                _logger.LogInformation("Invoice {Id} already exists in BC as {Number}", invoice.Id, existing.Number);
                return true;
            }
        }
        return false;
    }

    private async Task<bool> SkipAlreadyPushedCreditNoteAsync(Guid companyId, Invoice creditNote)
    {
        if (!string.IsNullOrEmpty(creditNote.BcInvoiceNumber))
        {
            _logger.LogInformation("Credit note {Id} already pushed to BC as {Number}", creditNote.Id, creditNote.BcInvoiceNumber);
            return true;
        }

        if (!string.IsNullOrEmpty(creditNote.InvoiceNumber))
        {
            var existing = await _bcClient.GetSalesCreditMemoByExternalDocAsync(companyId, creditNote.InvoiceNumber);
            if (existing is not null)
            {
                creditNote.BcInvoiceNumber = existing.Number;
                creditNote.BcInvoiceId = existing.Id;
                creditNote.InvoiceNumber = existing.Number;
                await _db.SaveChangesAsync();
                _logger.LogInformation("Credit note {Id} already exists in BC as {Number}", creditNote.Id, existing.Number);
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
        var lotSaleItemNo = await ResolveItemNumberAsync(companyId, "Lot Sale");
        var auctFeeItemNo = invoice.AuctionFee != 0 ? await ResolveItemNumberAsync(companyId, "Auction Fee") : null;
        var commItemNo = invoice.Commission != 0 ? await ResolveItemNumberAsync(companyId, "Commission") : null;

        int seq = 10000;
        foreach (var line in invoice.Lines)
        {
            var bcLine = new BcSalesInvoiceLine
            {
                DocumentId = documentId,
                Sequence = seq,
                LineType = "Item",
                LineObjectNumber = lotSaleItemNo,
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
                LineObjectNumber = auctFeeItemNo!, Description = "Auction Fee", Quantity = 1, UnitPrice = invoice.AuctionFee
            });
            seq += 10000;
        }

        if (invoice.Commission != 0)
        {
            await _bcClient.CreateSalesInvoiceLineAsync(companyId, documentId, new BcSalesInvoiceLine
            {
                DocumentId = documentId, Sequence = seq, LineType = "Item",
                LineObjectNumber = commItemNo!, Description = "Commission", Quantity = 1, UnitPrice = invoice.Commission
            });
        }
    }

    private async Task AddCreditMemoLinesToBcAsync(Guid companyId, Guid documentId, Invoice creditNote)
    {
        var lotSaleItemNo = await ResolveItemNumberAsync(companyId, "Lot Sale");
        var auctFeeItemNo = creditNote.AuctionFee != 0 ? await ResolveItemNumberAsync(companyId, "Auction Fee") : null;
        var commItemNo = creditNote.Commission != 0 ? await ResolveItemNumberAsync(companyId, "Commission") : null;

        int seq = 10000;
        foreach (var line in creditNote.Lines)
        {
            var bcLine = new BcSalesCreditMemoLine
            {
                DocumentId = documentId,
                Sequence = seq,
                LineType = "Item",
                LineObjectNumber = lotSaleItemNo,
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
                LineObjectNumber = auctFeeItemNo!, Description = "Auction Fee", Quantity = 1, UnitPrice = Math.Abs(creditNote.AuctionFee)
            });
            seq += 10000;
        }

        if (creditNote.Commission != 0)
        {
            await _bcClient.CreateSalesCreditMemoLineAsync(companyId, documentId, new BcSalesCreditMemoLine
            {
                DocumentId = documentId, Sequence = seq, LineType = "Item",
                LineObjectNumber = commItemNo!, Description = "Commission", Quantity = 1, UnitPrice = Math.Abs(creditNote.Commission)
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
    public async Task PushInvoiceToBcAsync(Invoice invoice)
    {
        var companyId = await _bcClient.ResolveCompanyIdAsync();

        if (await SkipAlreadyPushedInvoiceAsync(companyId, invoice)) return;

        var buyer = await ResolveBuyerAsBcCustomerAsync(companyId, invoice.Id, invoice.BuyerId, invoice.Buyer);
        if (buyer == null) return;

        var extDocRef = !string.IsNullOrEmpty(invoice.InvoiceNumber) ? invoice.InvoiceNumber : $"AUC-{invoice.Id}";
        var postingDate = DateTime.UtcNow;
        var bcInvoice = new BcSalesInvoice
        {
            ExternalDocumentNumber = extDocRef,
            InvoiceDate = postingDate.ToString("yyyy-MM-dd"),
            DueDate = (invoice.PromptDate ?? postingDate.AddDays(30)).ToString("yyyy-MM-dd"),
            CustomerId = buyer.Value.BcCustomerId,
            CurrencyCode = ""
        };

        var created = await _bcClient.CreateSalesInvoiceAsync(companyId, bcInvoice);
        await AddInvoiceLinesToBcAsync(companyId, created.Id, invoice);

        var posted = await _bcClient.PostSalesInvoiceAsync(companyId, created.Id, bcInvoice.ExternalDocumentNumber);
        var finalNumber = posted?.Number ?? created.Number;
        var finalId = posted?.Id ?? created.Id;

        invoice.BcInvoiceNumber = finalNumber;
        invoice.BcInvoiceId = finalId;
        invoice.InvoiceNumber = finalNumber;

        _logger.LogInformation("Posted invoice: finalNumber={FinalNumber}, finalId={FinalId}, postedWasNull={PostedNull}",
            finalNumber, finalId, posted == null);

        await TryFetchAndStorePdfAsync(companyId, finalId, invoice);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created and posted BC sales invoice {BcNumber} (customer={Customer})",
            invoice.InvoiceNumber, buyer.Value.BuyerNumber);
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
        var invoices = await _db.Set<Invoice>().Where(i => !i.IsCreditNote).CountAsync();
        var creditNotes = await _db.Set<Invoice>().Where(i => i.IsCreditNote).CountAsync();

        var brokersSynced = brokers.Count(b => b.BcSyncedAt.HasValue);
        var buyersSynced = buyers.Count(b => b.BcSyncedAt.HasValue);

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
            Invoices = invoices,
            CreditNotes = creditNotes
        };
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
            CurrencyCode = buyer.Currency == "EUR" ? "" : buyer.Currency,
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
            var pdfBytes = await _bcClient.GetSalesInvoicePdfAsync(companyId, bcInvoiceId);
            if (pdfBytes is null || pdfBytes.Length == 0)
            {
                _logger.LogWarning("No PDF returned from BC for invoice {Number}", invoice.InvoiceNumber);
                return;
            }

            var fileName = $"bc-{invoice.InvoiceNumber}.pdf";
            invoice.PdfUrl = await _blobStorage.UploadPdfAsync(fileName, pdfBytes);
            _logger.LogInformation("Stored BC invoice PDF for {Number} ({Bytes} bytes)", invoice.InvoiceNumber, pdfBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch/store BC invoice PDF for {Number}", invoice.InvoiceNumber);
        }
    }

    private async Task TryFetchAndStoreCreditMemoPdfAsync(Guid companyId, Guid bcCreditMemoId, Invoice creditNote)
    {
        try
        {
            var pdfBytes = await _bcClient.GetSalesCreditMemoPdfAsync(companyId, bcCreditMemoId);
            if (pdfBytes is null || pdfBytes.Length == 0)
            {
                _logger.LogWarning("No PDF returned from BC for credit memo {Number}", creditNote.InvoiceNumber);
                return;
            }

            var fileName = $"bc-{creditNote.InvoiceNumber}.pdf";
            creditNote.PdfUrl = await _blobStorage.UploadPdfAsync(fileName, pdfBytes);
            _logger.LogInformation("Stored BC credit memo PDF for {Number} ({Bytes} bytes)", creditNote.InvoiceNumber, pdfBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch/store BC credit memo PDF for {Number}", creditNote.InvoiceNumber);
        }
    }
}
