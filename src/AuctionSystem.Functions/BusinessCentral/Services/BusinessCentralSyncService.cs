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
                var existing = await _bcClient.GetSalesInvoiceByExternalDocAsync(companyId, invoice.InvoiceNumber);
                if (existing is not null)
                {
                    result.Skipped++;
                    _logger.LogInformation("Invoice {Number} already exists in BC, skipping", invoice.InvoiceNumber);
                    continue;
                }

                var buyerBcCustomer = await _bcClient.GetCustomerByNumberAsync(companyId, invoice.Buyer.BuyerNumber);
                if (buyerBcCustomer is null)
                {
                    result.Failed++;
                    result.Errors.Add($"Invoice {invoice.InvoiceNumber}: Buyer {invoice.Buyer.BuyerNumber} not found in BC. Sync buyers first.");
                    continue;
                }

                var bcInvoice = new BcSalesInvoice
                {
                    ExternalDocumentNumber = invoice.InvoiceNumber,
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

                // Post the invoice in BC
                await _bcClient.PostSalesInvoiceAsync(companyId, created.Id);

                // Store BC-assigned invoice number
                invoice.BcInvoiceNumber = created.Number;
                invoice.BcInvoiceId = created.Id;

                // Fetch PDF from BC and store in blob storage
                await TryFetchAndStorePdfAsync(companyId, created.Id, invoice);

                await _db.SaveChangesAsync();

                result.Created++;
                _logger.LogInformation("Created and posted BC sales invoice {BcNumber} for {Number}", created.Number, invoice.InvoiceNumber);
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
    /// </summary>
    public async Task<SyncResult> PushCreditNotesAsync()
    {
        var result = new SyncResult { Direction = "Push", EntityType = "Credit Note → BC Sales Credit Memo" };

        var creditNotes = await _db.Set<Invoice>()
            .Include(i => i.Buyer)
            .Include(i => i.Broker)
            .Include(i => i.Lines)
            .Where(i => i.IsCreditNote)
            .ToListAsync();

        result.TotalProcessed = creditNotes.Count;
        var companyId = await _bcClient.ResolveCompanyIdAsync();

        foreach (var cn in creditNotes)
        {
            try
            {
                var buyerBcCustomer = await _bcClient.GetCustomerByNumberAsync(companyId, cn.Buyer.BuyerNumber);
                if (buyerBcCustomer is null)
                {
                    result.Failed++;
                    result.Errors.Add($"Credit Note {cn.InvoiceNumber}: Buyer {cn.Buyer.BuyerNumber} not found in BC. Sync buyers first.");
                    continue;
                }

                var bcCreditMemo = new BcSalesCreditMemo
                {
                    ExternalDocumentNumber = cn.InvoiceNumber,
                    CreditMemoDate = cn.InvoiceDate.ToString("yyyy-MM-dd"),
                    CustomerId = buyerBcCustomer.Id,
                    CurrencyCode = cn.Currency == "EUR" ? "EUR" : cn.Currency
                };

                var created = await _bcClient.CreateSalesCreditMemoAsync(companyId, bcCreditMemo);

                int seq = 10000;
                foreach (var line in cn.Lines)
                {
                    var bcLine = new BcSalesCreditMemoLine
                    {
                        DocumentId = created.Id,
                        Sequence = seq,
                        LineType = "Comment",
                        Description = $"Lot {line.LotNumber}: {line.Description} ({line.Skins} skins)",
                        Quantity = Math.Abs(line.Skins),
                        UnitPrice = Math.Abs(line.PricePerSkin),
                        LineAmount = Math.Abs(line.HammerPrice)
                    };
                    await _bcClient.CreateSalesCreditMemoLineAsync(companyId, created.Id, bcLine);
                    seq += 10000;
                }

                result.Created++;
                _logger.LogInformation("Created BC sales credit memo for {Number}", cn.InvoiceNumber);
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
    /// Push a single invoice to BC as a Sales Invoice with G/L account lines.
    /// Account mapping: Lot Sale=2300, Auction Fee=4040, Commission=2310.
    /// </summary>
    public async Task PushInvoiceToBcAsync(Invoice invoice)
    {
        var companyId = await _bcClient.ResolveCompanyIdAsync();

        // Check if already pushed
        var existing = await _bcClient.GetSalesInvoiceByExternalDocAsync(companyId, invoice.InvoiceNumber);
        if (existing is not null)
        {
            _logger.LogInformation("Invoice {Number} already exists in BC, skipping", invoice.InvoiceNumber);
            return;
        }

        // Resolve buyer as BC customer
        var buyer = invoice.Buyer ?? await _db.Buyers.FindAsync(invoice.BuyerId);
        if (buyer == null)
        {
            _logger.LogWarning("Invoice {Number}: Buyer {Id} not found, skipping BC push", invoice.InvoiceNumber, invoice.BuyerId);
            return;
        }

        var buyerBcCustomer = await _bcClient.GetCustomerByNumberAsync(companyId, buyer.BuyerNumber);
        if (buyerBcCustomer is null)
        {
            _logger.LogWarning("Invoice {Number}: Buyer {Number} not found in BC, skipping", invoice.InvoiceNumber, buyer.BuyerNumber);
            return;
        }

        var bcInvoice = new BcSalesInvoice
        {
            ExternalDocumentNumber = invoice.InvoiceNumber,
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

        // Post the invoice in BC
        await _bcClient.PostSalesInvoiceAsync(companyId, created.Id);

        // Store BC-assigned invoice number
        invoice.BcInvoiceNumber = created.Number;
        invoice.BcInvoiceId = created.Id;

        // Fetch PDF from BC and store in blob storage
        await TryFetchAndStorePdfAsync(companyId, created.Id, invoice);

        await _db.SaveChangesAsync();

        _logger.LogInformation("Created and posted BC sales invoice for {Number} (customer={Customer})",
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
}
