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
        var companyId = await _bcClient.ResolveCompanyIdAsync();

        foreach (var invoice in invoices)
        {
            try
            {
                // Skip if already pushed
                if (!string.IsNullOrEmpty(invoice.BcInvoiceNumber))
                {
                    result.Skipped++;
                    continue;
                }

                // Check by external doc if we have an invoice number
                if (!string.IsNullOrEmpty(invoice.InvoiceNumber))
                {
                    var existing = await _bcClient.GetSalesInvoiceByExternalDocAsync(companyId, invoice.InvoiceNumber);
                    if (existing is not null)
                    {
                        invoice.BcInvoiceNumber = existing.Number;
                        invoice.BcInvoiceId = existing.Id;
                        invoice.InvoiceNumber = existing.Number;
                        await _db.SaveChangesAsync();
                        result.Skipped++;
                        _logger.LogInformation("Invoice {Id} already exists in BC as {Number}", invoice.Id, existing.Number);
                        continue;
                    }
                }

                var buyerBcCustomer = await _bcClient.GetCustomerByNumberAsync(companyId, invoice.Buyer.BuyerNumber);
                if (buyerBcCustomer is null)
                {
                    result.Failed++;
                    result.Errors.Add($"Invoice {invoice.InvoiceNumber}: Buyer {invoice.Buyer.BuyerNumber} not found in BC. Sync buyers first.");
                    continue;
                }

                var extDoc = !string.IsNullOrEmpty(invoice.InvoiceNumber) ? invoice.InvoiceNumber : $"AUC-{invoice.Id}";
                var bcInvoice = new BcSalesInvoice
                {
                    ExternalDocumentNumber = extDoc,
                    InvoiceDate = invoice.InvoiceDate.ToString("yyyy-MM-dd"),
                    DueDate = (invoice.PromptDate ?? invoice.InvoiceDate.AddDays(30)).ToString("yyyy-MM-dd"),
                    CustomerId = buyerBcCustomer.Id,
                    CurrencyCode = ""
                };

                var created = await _bcClient.CreateSalesInvoiceAsync(companyId, bcInvoice);

                int seq = 10000;
                foreach (var line in invoice.Lines)
                {
                    var bcLine = new BcSalesInvoiceLine
                    {
                        DocumentId = created.Id,
                        Sequence = seq,
                        LineType = "Item",
                        LineObjectNumber = "LOTSALE",
                        Description = $"Lot {line.LotNumber}: {line.Description} ({line.Skins} skins)",
                        Quantity = line.Skins,
                        UnitPrice = line.PricePerSkin
                    };
                    await _bcClient.CreateSalesInvoiceLineAsync(companyId, created.Id, bcLine);
                    seq += 10000;
                }

                if (invoice.AuctionFee != 0)
                {
                    var feeLine = new BcSalesInvoiceLine
                    {
                        DocumentId = created.Id,
                        Sequence = seq,
                        LineType = "Item",
                        LineObjectNumber = "AUCTFEE",
                        Description = "Auction Fee",
                        Quantity = 1,
                        UnitPrice = invoice.AuctionFee
                    };
                    await _bcClient.CreateSalesInvoiceLineAsync(companyId, created.Id, feeLine);
                    seq += 10000;
                }

                if (invoice.Commission != 0)
                {
                    var commLine = new BcSalesInvoiceLine
                    {
                        DocumentId = created.Id,
                        Sequence = seq,
                        LineType = "Item",
                        LineObjectNumber = "BROKERCOMM",
                        Description = "Commission",
                        Quantity = 1,
                        UnitPrice = invoice.Commission
                    };
                    await _bcClient.CreateSalesInvoiceLineAsync(companyId, created.Id, commLine);
                }

                // Post the invoice in BC and get the posted number
                var posted = await _bcClient.PostSalesInvoiceAsync(companyId, created.Id);
                var finalNumber = posted?.Number ?? created.Number;
                var finalId = posted?.Id ?? created.Id;

                // Store BC-assigned invoice number as THE invoice number
                invoice.BcInvoiceNumber = finalNumber;
                invoice.BcInvoiceId = finalId;
                invoice.InvoiceNumber = finalNumber;

                // Fetch PDF from BC and store in blob storage
                await TryFetchAndStorePdfAsync(companyId, finalId, invoice);

                await _db.SaveChangesAsync();

                result.Created++;
                _logger.LogInformation("Created and posted BC sales invoice {BcNumber}", finalNumber);
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

        // Skip if already pushed
        if (!string.IsNullOrEmpty(creditNote.BcInvoiceNumber))
        {
            _logger.LogInformation("Credit note {Id} already pushed to BC as {Number}", creditNote.Id, creditNote.BcInvoiceNumber);
            return;
        }

        // Check if already exists in BC by external doc number
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
                return;
            }
        }

        // Resolve buyer as BC customer
        var buyer = creditNote.Buyer ?? await _db.Buyers.FindAsync(creditNote.BuyerId);
        if (buyer == null)
        {
            _logger.LogWarning("Credit note {Id}: Buyer {BuyerId} not found, skipping BC push", creditNote.Id, creditNote.BuyerId);
            return;
        }

        var buyerBcCustomer = await _bcClient.GetCustomerByNumberAsync(companyId, buyer.BuyerNumber);
        if (buyerBcCustomer is null)
        {
            _logger.LogWarning("Credit note {Id}: Buyer {Number} not found in BC, skipping", creditNote.Id, buyer.BuyerNumber);
            return;
        }

        // Build external document number from the original invoice's BC number
        var extDocNumber = !string.IsNullOrEmpty(creditNote.InvoiceNumber) ? creditNote.InvoiceNumber : $"CN-{creditNote.Id}";

        var bcCreditMemo = new BcSalesCreditMemo
        {
            ExternalDocumentNumber = extDocNumber,
            CreditMemoDate = creditNote.InvoiceDate.ToString("yyyy-MM-dd"),
            CustomerId = buyerBcCustomer.Id,
            CurrencyCode = ""
        };

        var created = await _bcClient.CreateSalesCreditMemoAsync(companyId, bcCreditMemo);

        int seq = 10000;

        // Lot sale lines — Item LOTSALE (use absolute values for credit memo)
        foreach (var line in creditNote.Lines)
        {
            var bcLine = new BcSalesCreditMemoLine
            {
                DocumentId = created.Id,
                Sequence = seq,
                LineType = "Item",
                LineObjectNumber = "LOTSALE",
                Description = $"Lot {line.LotNumber}: {line.Description} ({Math.Abs(line.Skins)} skins)",
                Quantity = Math.Abs(line.Skins),
                UnitPrice = Math.Abs(line.PricePerSkin)
            };
            await _bcClient.CreateSalesCreditMemoLineAsync(companyId, created.Id, bcLine);
            seq += 10000;
        }

        // Auction Fee line — Item AUCTFEE
        if (creditNote.AuctionFee != 0)
        {
            var feeLine = new BcSalesCreditMemoLine
            {
                DocumentId = created.Id,
                Sequence = seq,
                LineType = "Item",
                LineObjectNumber = "AUCTFEE",
                Description = "Auction Fee",
                Quantity = 1,
                UnitPrice = Math.Abs(creditNote.AuctionFee)
            };
            await _bcClient.CreateSalesCreditMemoLineAsync(companyId, created.Id, feeLine);
            seq += 10000;
        }

        // Commission line — Item BROKERCOMM
        if (creditNote.Commission != 0)
        {
            var commLine = new BcSalesCreditMemoLine
            {
                DocumentId = created.Id,
                Sequence = seq,
                LineType = "Item",
                LineObjectNumber = "BROKERCOMM",
                Description = "Commission",
                Quantity = 1,
                UnitPrice = Math.Abs(creditNote.Commission)
            };
            await _bcClient.CreateSalesCreditMemoLineAsync(companyId, created.Id, commLine);
        }

        // Post the credit memo in BC and get the posted number
        var posted = await _bcClient.PostSalesCreditMemoAsync(companyId, created.Id);
        var finalNumber = posted?.Number ?? created.Number;
        var finalId = posted?.Id ?? created.Id;

        // Store BC-assigned number
        creditNote.BcInvoiceNumber = finalNumber;
        creditNote.BcInvoiceId = finalId;
        creditNote.InvoiceNumber = finalNumber;

        // Fetch PDF from BC and store in blob storage
        await TryFetchAndStoreCreditMemoPdfAsync(companyId, finalId, creditNote);

        await _db.SaveChangesAsync();

        _logger.LogInformation("Created and posted BC sales credit memo {BcNumber} (customer={Customer})",
            creditNote.InvoiceNumber, buyer.BuyerNumber);

        // Apply credit memo against original invoice in BC
        await TryApplyCreditMemoToInvoiceAsync(companyId, creditNote, buyer.BuyerNumber);
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

        // Check if already pushed (by BC reference or external doc)
        var extDocRef = !string.IsNullOrEmpty(invoice.InvoiceNumber) ? invoice.InvoiceNumber : $"AUC-{invoice.Id}";
        if (!string.IsNullOrEmpty(invoice.BcInvoiceNumber))
        {
            _logger.LogInformation("Invoice {Id} already pushed to BC as {Number}", invoice.Id, invoice.BcInvoiceNumber);
            return;
        }
        var existing = !string.IsNullOrEmpty(invoice.InvoiceNumber)
            ? await _bcClient.GetSalesInvoiceByExternalDocAsync(companyId, invoice.InvoiceNumber)
            : null;
        if (existing is not null)
        {
            invoice.BcInvoiceNumber = existing.Number;
            invoice.BcInvoiceId = existing.Id;
            await _db.SaveChangesAsync();
            _logger.LogInformation("Invoice {Id} already exists in BC as {Number}", invoice.Id, existing.Number);
            return;
        }

        // Resolve buyer as BC customer
        var buyer = invoice.Buyer ?? await _db.Buyers.FindAsync(invoice.BuyerId);
        if (buyer == null)
        {
            _logger.LogWarning("Invoice {Id}: Buyer {BuyerId} not found, skipping BC push", invoice.Id, invoice.BuyerId);
            return;
        }

        var buyerBcCustomer = await _bcClient.GetCustomerByNumberAsync(companyId, buyer.BuyerNumber);
        if (buyerBcCustomer is null)
        {
            _logger.LogWarning("Invoice {Id}: Buyer {Number} not found in BC, skipping", invoice.Id, buyer.BuyerNumber);
            return;
        }

        var bcInvoice = new BcSalesInvoice
        {
            ExternalDocumentNumber = extDocRef,
            InvoiceDate = invoice.InvoiceDate.ToString("yyyy-MM-dd"),
            DueDate = (invoice.PromptDate ?? invoice.InvoiceDate.AddDays(30)).ToString("yyyy-MM-dd"),
            CustomerId = buyerBcCustomer.Id,
            CurrencyCode = ""
        };

        var created = await _bcClient.CreateSalesInvoiceAsync(companyId, bcInvoice);

        int seq = 10000;

        // Lot sale lines — Item LOTSALE
        foreach (var line in invoice.Lines)
        {
            var bcLine = new BcSalesInvoiceLine
            {
                DocumentId = created.Id,
                Sequence = seq,
                LineType = "Item",
                LineObjectNumber = "LOTSALE",
                Description = $"Lot {line.LotNumber}: {line.Description} ({line.Skins} skins)",
                Quantity = line.Skins,
                UnitPrice = line.PricePerSkin
            };
            await _bcClient.CreateSalesInvoiceLineAsync(companyId, created.Id, bcLine);
            seq += 10000;
        }

        // Auction Fee line — Item AUCTFEE
        if (invoice.AuctionFee != 0)
        {
            var feeLine = new BcSalesInvoiceLine
            {
                DocumentId = created.Id,
                Sequence = seq,
                LineType = "Item",
                LineObjectNumber = "AUCTFEE",
                Description = "Auction Fee",
                Quantity = 1,
                UnitPrice = invoice.AuctionFee
            };
            await _bcClient.CreateSalesInvoiceLineAsync(companyId, created.Id, feeLine);
            seq += 10000;
        }

        // Commission line — Item BROKERCOMM
        if (invoice.Commission != 0)
        {
            var commLine = new BcSalesInvoiceLine
            {
                DocumentId = created.Id,
                Sequence = seq,
                LineType = "Item",
                LineObjectNumber = "BROKERCOMM",
                Description = "Commission",
                Quantity = 1,
                UnitPrice = invoice.Commission
            };
            await _bcClient.CreateSalesInvoiceLineAsync(companyId, created.Id, commLine);
        }

        // Post the invoice in BC and get the posted number (may differ from draft number)
        var posted = await _bcClient.PostSalesInvoiceAsync(companyId, created.Id);
        var finalNumber = posted?.Number ?? created.Number;
        var finalId = posted?.Id ?? created.Id;

        // Store BC-assigned invoice number as THE invoice number
        invoice.BcInvoiceNumber = finalNumber;
        invoice.BcInvoiceId = finalId;
        invoice.InvoiceNumber = finalNumber;

        // Fetch PDF from BC and store in blob storage
        await TryFetchAndStorePdfAsync(companyId, finalId, invoice);

        await _db.SaveChangesAsync();

        _logger.LogInformation("Created and posted BC sales invoice {BcNumber} (customer={Customer})",
            invoice.InvoiceNumber, buyer.BuyerNumber);
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

        return new
        {
            Brokers = new
            {
                Total = brokers.Count,
                Synced = brokers.Count,
                Unsynced = 0
            },
            Buyers = new
            {
                Total = buyers.Count,
                Synced = buyers.Count,
                Unsynced = 0
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

    private static BcVendor MapBrokerToVendor(Broker broker)
    {
        return new BcVendor
        {
            Number = broker.BrokerNumber,
            DisplayName = broker.CompanyName,
            AddressLine1 = broker.AddressLine1,
            AddressLine2 = broker.AddressLine2,
            City = broker.City,
            Country = broker.Country,
            PostalCode = broker.PostalCode,
            PhoneNumber = broker.ContactPhone,
            Email = broker.ContactEmail,
            Website = broker.HomePage,
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
            AddressLine1 = buyer.AddressLine1,
            AddressLine2 = buyer.AddressLine2,
            City = buyer.City,
            Country = buyer.Country,
            PostalCode = buyer.PostalCode,
            PhoneNumber = buyer.ContactPhone,
            Email = buyer.ContactEmail,
            Website = buyer.HomePage,
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
            AddressLine1 = farmer.AddressLine1,
            AddressLine2 = farmer.AddressLine2,
            City = farmer.City,
            Country = farmer.Country,
            PostalCode = farmer.PostalCode,
            PhoneNumber = farmer.ContactPhone,
            Email = farmer.ContactEmail,
            Website = farmer.HomePage,
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
