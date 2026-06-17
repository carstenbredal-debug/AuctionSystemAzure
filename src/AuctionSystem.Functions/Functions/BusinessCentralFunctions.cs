using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Functions.BusinessCentral.Configuration;
using AuctionSystem.Functions.BusinessCentral.Models;
using AuctionSystem.Functions.BusinessCentral.Services;
using AuctionSystem.Functions.Services;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

// All Business Central integration/sync/diagnostic endpoints are admin-only (class-level guard).
[AuctionSystem.Functions.Auth.RequireRole("Admin")]
public class BusinessCentralFunctions
{
    private readonly BusinessCentralSyncService? _syncService;
    private readonly BusinessCentralApiClient? _bcClient;
    private readonly BusinessCentralOptions _options;
    private readonly AuctionDbContext _db;
    private readonly ILogger<BusinessCentralFunctions> _logger;
    private readonly BlobStorageService? _blobStorage;

    public BusinessCentralFunctions(
        IOptions<BusinessCentralOptions> options,
        AuctionDbContext db,
        ILogger<BusinessCentralFunctions> logger,
        BusinessCentralSyncService? syncService = null,
        BusinessCentralApiClient? bcClient = null,
        BlobStorageService? blobStorage = null)
    {
        _options = options.Value;
        _db = db;
        _logger = logger;
        _syncService = syncService;
        _bcClient = bcClient;
        _blobStorage = blobStorage;
    }

    [Function("BcGetStatus")]
    public async Task<HttpResponseData> GetStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/status")] HttpRequestData req)
    {
        if (!_options.IsConfigured || _syncService is null)
            return await JsonResponse(req, new { configured = false, message = "Business Central is not configured. Set BC_TENANT_ID, BC_CLIENT_ID, BC_CLIENT_SECRET environment variables." });

        var status = await _syncService.GetSyncStatusAsync();
        return await JsonResponse(req, new { configured = true, status });
    }

    [Function("BcGetCompanies")]
    public async Task<HttpResponseData> GetCompanies(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/companies")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companies = await _bcClient!.GetCompaniesAsync();
        return await JsonResponse(req, companies);
    }

    [Function("BcGetCustomers")]
    public async Task<HttpResponseData> GetCustomers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/customers")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var customers = await _syncService!.PullCustomersAsync();
        return await JsonResponse(req, customers);
    }

    [Function("BcGetItems")]
    public async Task<HttpResponseData> GetItems(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/items")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var items = await _syncService!.PullItemsAsync();
        return await JsonResponse(req, items);
    }

    [Function("BcGetCountriesRegions")]
    public async Task<HttpResponseData> GetCountriesRegions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/countries")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();
        var countries = await _bcClient.GetCountriesRegionsAsync(companyId);
        return await JsonResponse(req, countries);
    }

    [Function("BcGetPaymentTerms")]
    public async Task<HttpResponseData> GetPaymentTerms(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/payment-terms")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();
        var terms = await _bcClient.GetPaymentTermsAsync(companyId);
        return await JsonResponse(req, terms);
    }

    [Function("BcGetCurrencies")]
    public async Task<HttpResponseData> GetCurrencies(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/currencies")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();
        var currencies = await _bcClient.GetCurrenciesAsync(companyId);
        return await JsonResponse(req, currencies);
    }

    [Function("BcGetDimensions")]
    public async Task<HttpResponseData> GetDimensions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/dimensions")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();
        var dims = await _bcClient.GetDimensionsAsync(companyId);

        // Map the codes this app relies on to their systemIds, ready to paste into the BC_DIM_* settings.
        var vendorType = dims.FirstOrDefault(d => string.Equals(d.Code, "VENDORTYPE", StringComparison.OrdinalIgnoreCase));
        var customerType = dims.FirstOrDefault(d => string.Equals(d.Code, "CUSTOMERTYPE", StringComparison.OrdinalIgnoreCase));
        Guid? ValId(BcDimension? d, string code) =>
            d?.DimensionValues.FirstOrDefault(v => string.Equals(v.Code, code, StringComparison.OrdinalIgnoreCase))?.Id;

        var suggestedSettings = new Dictionary<string, Guid?>
        {
            ["BC_DIM_VENDORTYPE_ID"] = vendorType?.Id,
            ["BC_DIM_VENDORTYPE_BROKER_VALUE_ID"] = ValId(vendorType, "BROKER"),
            ["BC_DIM_VENDORTYPE_FARMER_VALUE_ID"] = ValId(vendorType, "FARMER"),
            ["BC_DIM_CUSTOMERTYPE_ID"] = customerType?.Id,
            ["BC_DIM_CUSTOMERTYPE_BUYER_VALUE_ID"] = ValId(customerType, "BUYER")
        };
        var missing = suggestedSettings.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList();

        return await JsonResponse(req, new
        {
            companyId,
            suggestedSettings,
            missing = missing.Count > 0 ? (object)missing : null,
            dimensions = dims
        });
    }

    [Function("BcGetGenBusPostingGroups")]
    public async Task<HttpResponseData> GetGenBusPostingGroups(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/gen-bus-posting-groups")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyName = await ResolveCompanyNameAsync();
        var groups = await _bcClient!.GetGenBusinessPostingGroupsAsync(companyName);
        return await JsonResponse(req, groups);
    }

    [Function("BcGetVatBusPostingGroups")]
    public async Task<HttpResponseData> GetVatBusPostingGroups(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/vat-bus-posting-groups")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyName = await ResolveCompanyNameAsync();
        var groups = await _bcClient!.GetVatBusinessPostingGroupsAsync(companyName);
        return await JsonResponse(req, groups);
    }

    [Function("BcGetCustomerPostingGroups")]
    public async Task<HttpResponseData> GetCustomerPostingGroups(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/customer-posting-groups")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyName = await ResolveCompanyNameAsync();
        var groups = await _bcClient!.GetCustomerPostingGroupsAsync(companyName);
        return await JsonResponse(req, groups);
    }

    [Function("BcGetVendorPostingGroups")]
    public async Task<HttpResponseData> GetVendorPostingGroups(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/vendor-posting-groups")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();
        var companyName = await ResolveCompanyNameAsync();
        var groups = await _bcClient!.GetVendorPostingGroupsAsync(companyId, companyName);
        return await JsonResponse(req, groups);
    }

    [Function("BcGetVendors")]
    public async Task<HttpResponseData> GetVendors(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/vendors")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();
        var vendors = await _bcClient.GetVendorsAsync(companyId);
        return await JsonResponse(req, vendors);
    }

    [Function("BcSyncBrokers")]
    public async Task<HttpResponseData> SyncBrokers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bc/sync/brokers")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        _logger.LogInformation("Syncing brokers to Business Central");
        var result = await _syncService!.PushBrokersAsync();
        return await JsonResponse(req, result);
    }

    [Function("BcSyncFarmers")]
    public async Task<HttpResponseData> SyncFarmers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bc/sync/farmers")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        _logger.LogInformation("Syncing farmers to Business Central");
        var result = await _syncService!.PushFarmersAsync();
        return await JsonResponse(req, result);
    }

    [Function("BcSyncBuyers")]
    public async Task<HttpResponseData> SyncBuyers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bc/sync/buyers")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        _logger.LogInformation("Syncing buyers to Business Central");
        var result = await _syncService!.PushBuyersAsync();
        return await JsonResponse(req, result);
    }

    [Function("BcSyncInvoices")]
    public async Task<HttpResponseData> SyncInvoices(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bc/sync/invoices")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        _logger.LogInformation("Syncing invoices to Business Central");
        var result = await _syncService!.PushInvoicesAsync();
        return await JsonResponse(req, result);
    }

    [Function("BcRefreshInvoicePdf")]
    public async Task<HttpResponseData> RefreshInvoicePdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bc/invoices/{invoiceId:int}/refresh-pdf")] HttpRequestData req, int invoiceId)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var invoice = await _db.Invoices.FindAsync(invoiceId);
        if (invoice == null)
            return await JsonResponse(req, new { error = "Invoice not found" }, HttpStatusCode.NotFound);
        if (invoice.BcInvoiceId == null)
            return await JsonResponse(req, new { error = "Invoice has no BC ID — not yet pushed to BC" }, HttpStatusCode.BadRequest);

        var companyId = await _syncService!.GetCompanyIdAsync();
        await _syncService.RefreshPdfAsync(companyId, invoice);
        await _db.SaveChangesAsync();

        return await JsonResponse(req, new { invoiceId = invoice.Id, pdfUrl = invoice.PdfUrl });
    }

    [Function("BcSyncCreditNotes")]
    public async Task<HttpResponseData> SyncCreditNotes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bc/sync/credit-notes")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        _logger.LogInformation("Syncing credit notes to Business Central");
        var result = await _syncService!.PushCreditNotesAsync();
        return await JsonResponse(req, result);
    }

    [Function("BcSyncAll")]
    public async Task<HttpResponseData> SyncAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bc/sync/all")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        _logger.LogInformation("Starting full Business Central sync");
        var results = await _syncService!.RunFullSyncAsync();
        return await JsonResponse(req, results);
    }

    private async Task<string> ResolveCompanyNameAsync()
    {
        var companyId = await _bcClient!.ResolveCompanyIdAsync();
        var companies = await _bcClient.GetCompaniesAsync();
        var company = companies.FirstOrDefault(c => c.Id == companyId);
        // Use Name (internal) not DisplayName — OData endpoints require the internal name
        return company?.Name ?? companies.First().Name;
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("BcResetInvoices")]
    public async Task<HttpResponseData> ResetInvoices(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bc/reset-invoices")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var err))
            return await JsonResponse(req, err!, HttpStatusCode.ServiceUnavailable);

        var companyId = Guid.Parse(_options.CompanyId);
        _logger.LogWarning("RESETTING BC Sales Invoices in company {CompanyId}", companyId);

        var invoices = await _bcClient!.GetSalesInvoicesAsync(companyId);
        int deleted = 0, skipped = 0;
        var errors = new List<string>();

        foreach (var inv in invoices)
        {
            try
            {
                await _bcClient.DeleteSalesInvoiceAsync(companyId, inv.Id, inv.ETag);
                deleted++;
                _logger.LogInformation("Deleted BC invoice {Number} ({Id})", inv.Number, inv.Id);
            }
            catch (Exception ex)
            {
                skipped++;
                errors.Add($"{inv.Number}: {ex.Message}");
                _logger.LogWarning(ex, "Could not delete BC invoice {Number}", inv.Number);
            }
        }

        return await JsonResponse(req, new
        {
            message = $"BC invoice reset complete",
            total = invoices.Count,
            deleted,
            skipped,
            errors = errors.Count > 0 ? (object)errors : null
        });
    }

    [Function("BcClearInvoiceRefs")]
    public async Task<HttpResponseData> ClearInvoiceRefs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bc/clear-invoice-refs")] HttpRequestData req)
    {
        // Clear all local BC references so invoices can be re-pushed
        var invoices = await _db.Invoices
            .Where(i => !i.IsCreditNote && (i.BcInvoiceNumber != null && i.BcInvoiceNumber != ""))
            .ToListAsync();

        foreach (var inv in invoices)
        {
            inv.BcInvoiceNumber = null;
            inv.BcInvoiceId = null;
            inv.InvoiceNumber = "";
            inv.PdfUrl = null;
        }

        await _db.SaveChangesAsync();

        return await JsonResponse(req, new
        {
            message = $"Cleared BC references on {invoices.Count} invoice(s). They will appear as unpushed.",
            cleared = invoices.Count
        });
    }

    [Function("BcConsistencyCheck")]
    public async Task<HttpResponseData> ConsistencyCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/consistency-check")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var err))
            return await JsonResponse(req, err!, HttpStatusCode.ServiceUnavailable);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();
        var mismatches = new List<object>();

        var brokers = await _db.Brokers.Where(b => b.IsActive).ToListAsync();
        await CheckBrokerConsistency(companyId, brokers, mismatches);

        var buyers = await _db.Buyers.Where(b => b.IsActive).ToListAsync();
        await CheckBuyerConsistency(companyId, buyers, mismatches);

        var farmers = await _db.Farmers.Where(f => f.IsActive).ToListAsync();
        await CheckFarmerConsistency(companyId, farmers, mismatches);

        return await JsonResponse(req, new
        {
            message = mismatches.Count == 0 ? "All consistent" : $"{mismatches.Count} mismatch(es) found",
            checked_ = new { brokers = brokers.Count, buyers = buyers.Count, farmers = farmers.Count },
            mismatches
        });
    }

    private async Task CheckBrokerConsistency(Guid companyId, List<Broker> brokers, List<object> mismatches)
    {
        foreach (var broker in brokers)
        {
            var bcVendor = await _bcClient!.GetVendorByNumberAsync(companyId, broker.BrokerNumber);
            if (bcVendor is null)
            {
                mismatches.Add(new { entity = "Broker", number = broker.BrokerNumber, name = broker.CompanyName, field = "BC Vendor", auction = "exists", bc = "NOT FOUND" });
                continue;
            }

            var localCurrency = broker.Currency == "EUR" ? "" : broker.Currency;
            AddMismatchIfDifferent(mismatches, "Broker", broker.BrokerNumber, broker.CompanyName, "GenBusPostingGroup", broker.GenBusPostingGroup, bcVendor.GenBusPostingGroup);
            AddMismatchIfDifferent(mismatches, "Broker", broker.BrokerNumber, broker.CompanyName, "VatBusPostingGroup", broker.VatBusPostingGroup, bcVendor.VatBusPostingGroup);
            AddCurrencyMismatchIfDifferent(mismatches, "Broker", broker.BrokerNumber, broker.CompanyName, localCurrency, broker.Currency, bcVendor.CurrencyCode);
            AddMismatchIfDifferent(mismatches, "Broker", broker.BrokerNumber, broker.CompanyName, "Country", broker.Country, bcVendor.Country);
        }
    }

    private async Task CheckBuyerConsistency(Guid companyId, List<Buyer> buyers, List<object> mismatches)
    {
        foreach (var buyer in buyers)
        {
            var bcCustomer = await _bcClient!.GetCustomerByNumberAsync(companyId, buyer.BuyerNumber);
            if (bcCustomer is null)
            {
                mismatches.Add(new { entity = "Buyer", number = buyer.BuyerNumber, name = buyer.Name, field = "BC Customer", auction = "exists", bc = "NOT FOUND" });
                continue;
            }

            var localCurrency = buyer.Currency == "EUR" ? "" : buyer.Currency;
            AddMismatchIfDifferent(mismatches, "Buyer", buyer.BuyerNumber, buyer.Name, "GenBusPostingGroup", buyer.GenBusPostingGroup, bcCustomer.GenBusPostingGroup);
            AddMismatchIfDifferent(mismatches, "Buyer", buyer.BuyerNumber, buyer.Name, "VatBusPostingGroup", buyer.VatBusPostingGroup, bcCustomer.VatBusPostingGroup);
            AddMismatchIfDifferent(mismatches, "Buyer", buyer.BuyerNumber, buyer.Name, "CustomerPostingGroup", buyer.CustomerPostingGroup, bcCustomer.CustomerPostingGroup);
            AddCurrencyMismatchIfDifferent(mismatches, "Buyer", buyer.BuyerNumber, buyer.Name, localCurrency, buyer.Currency, bcCustomer.CurrencyCode);
            AddMismatchIfDifferent(mismatches, "Buyer", buyer.BuyerNumber, buyer.Name, "Country", buyer.Country, bcCustomer.Country);
        }
    }

    private async Task CheckFarmerConsistency(Guid companyId, List<Farmer> farmers, List<object> mismatches)
    {
        foreach (var farmer in farmers)
        {
            var bcVendor = await _bcClient!.GetVendorByNumberAsync(companyId, farmer.FarmerNumber);
            if (bcVendor is null)
            {
                mismatches.Add(new { entity = "Farmer", number = farmer.FarmerNumber, name = farmer.Name, field = "BC Vendor", auction = "exists", bc = "NOT FOUND" });
                continue;
            }

            var localCurrency = farmer.Currency == "EUR" ? "" : farmer.Currency;
            AddMismatchIfDifferent(mismatches, "Farmer", farmer.FarmerNumber, farmer.Name, "GenBusPostingGroup", farmer.GenBusPostingGroup, bcVendor.GenBusPostingGroup);
            AddMismatchIfDifferent(mismatches, "Farmer", farmer.FarmerNumber, farmer.Name, "VatBusPostingGroup", farmer.VatBusPostingGroup, bcVendor.VatBusPostingGroup);
            AddMismatchIfDifferent(mismatches, "Farmer", farmer.FarmerNumber, farmer.Name, "VendorPostingGroup", farmer.VendorPostingGroup, bcVendor.VendorPostingGroup);
            AddCurrencyMismatchIfDifferent(mismatches, "Farmer", farmer.FarmerNumber, farmer.Name, localCurrency, farmer.Currency, bcVendor.CurrencyCode);
            AddMismatchIfDifferent(mismatches, "Farmer", farmer.FarmerNumber, farmer.Name, "Country", farmer.Country, bcVendor.Country);
        }
    }

    private static void AddMismatchIfDifferent(List<object> mismatches, string entity, string number, string name, string field, string? localValue, string? bcValue)
    {
        if (!string.IsNullOrEmpty(localValue) && localValue != bcValue)
            mismatches.Add(new { entity, number, name, field, auction = localValue, bc = bcValue });
    }

    private static void AddCurrencyMismatchIfDifferent(List<object> mismatches, string entity, string number, string name, string localCurrency, string? originalCurrency, string? bcCurrencyCode)
    {
        if (!string.IsNullOrEmpty(localCurrency) && localCurrency != bcCurrencyCode)
            mismatches.Add(new { entity, number, name, field = "CurrencyCode", auction = originalCurrency, bc = string.IsNullOrEmpty(bcCurrencyCode) ? "(LCY)" : bcCurrencyCode });
    }

    [Function("BcPaymentConsistencyCheck")]
    public async Task<HttpResponseData> PaymentConsistencyCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/payment-consistency-check")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var err))
            return await JsonResponse(req, err!, HttpStatusCode.ServiceUnavailable);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();

        // Get all invoices that have been pushed to BC (have a BcInvoiceNumber)
        var invoices = await _db.Invoices
            .Include(i => i.Buyer)
            .Where(i => !string.IsNullOrEmpty(i.BcInvoiceNumber))
            .ToListAsync();

        // Get all customer ledger entries from BC
        var allLedgerEntries = await _bcClient.GetCustomerLedgerEntriesAsync(companyId);

        // Build lookup: document number -> ledger entry
        var invoiceLedgerEntries = allLedgerEntries
            .Where(e => e.DocumentType == "Invoice")
            .GroupBy(e => e.DocumentNo)
            .ToDictionary(g => g.Key, g => g.First());

        var paymentLedgerEntries = allLedgerEntries
            .Where(e => e.DocumentType == "Payment" && e.Open)
            .GroupBy(e => e.CustomerNo)
            .ToDictionary(g => g.Key, g => g.ToList());

        var results = invoices.Select(invoice =>
        {
            var bcInvNo = invoice.BcInvoiceNumber!;
            var localStatus = invoice.Status.ToString();
            var buyerNo = invoice.Buyer?.BuyerNumber ?? "";
            var bcEntry = invoiceLedgerEntries.GetValueOrDefault(bcInvNo);
            var bcStatus = bcEntry == null ? "Not Found" : (bcEntry.Open ? "Open" : "Closed");
            var openPayments = paymentLedgerEntries.GetValueOrDefault(buyerNo) ?? new List<BcCustomerLedgerEntry>();

            return new
            {
                invoiceId = invoice.Id,
                bcInvoiceNumber = bcInvNo,
                buyerNumber = buyerNo,
                buyerName = invoice.Buyer?.Name ?? "",
                localStatus,
                bcStatus,
                invoiceAmount = invoice.TotalAmount,
                bcRemainingAmount = bcEntry?.RemainingAmount ?? 0m,
                openPaymentAvailable = openPayments.Sum(p => Math.Abs(p.RemainingAmount)),
                issue = DetectPaymentMismatch(localStatus, bcStatus)
            };
        }).ToList();

        var mismatches = results.Where(r => ((dynamic)r).issue != null).ToList();

        return await JsonResponse(req, new
        {
            message = mismatches.Count == 0 ? "All payment statuses consistent" : $"{mismatches.Count} mismatch(es) found",
            totalChecked = invoices.Count,
            mismatches = mismatches.Count,
            invoices = results
        });
    }

    [Function("DiagConnections")]
    public async Task<HttpResponseData> GetConnections(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diag/connections")] HttpRequestData req)
    {
        var (bcConnected, bcCompanyName, bcError) = await CheckBcConnectionAsync();
        var (sqlConnected, sqlServer, sqlDatabase, sqlError) = await CheckSqlConnectionAsync();

        return await JsonResponse(req, new
        {
            businessCentral = new
            {
                configured = _options.IsConfigured,
                connected = bcConnected,
                tenantId = _options.TenantId,
                environment = _options.Environment,
                companyId = _options.CompanyId,
                companyName = bcCompanyName,
                apiUrl = _options.IsConfigured ? _options.BaseUrl : "",
                error = bcError
            },
            azure = new
            {
                sql = new { connected = sqlConnected, server = sqlServer, database = sqlDatabase, error = sqlError },
                blobStorage = new { configured = _blobStorage is not null }
            }
        });
    }

    private async Task<(bool Connected, string CompanyName, string Error)> CheckBcConnectionAsync()
    {
        if (!_options.IsConfigured || _bcClient is null)
            return (false, "", "");

        try
        {
            var companies = await _bcClient.GetCompaniesAsync();
            if (companies.Count == 0)
                return (false, "", $"No companies found. API URL: {_options.BaseUrl}/companies. Check BC_ENVIRONMENT='{_options.Environment}' and ensure the App Registration has admin consent granted and permission sets assigned in BC.");

            // Try to find by CompanyId first, then by CompanyName
            var target = companies.FirstOrDefault(c => c.Id.ToString() == _options.CompanyId);
            if (target == null && !string.IsNullOrEmpty(_options.CompanyName))
                target = companies.FirstOrDefault(c => c.DisplayName.Equals(_options.CompanyName, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                target = companies.FirstOrDefault();

            var availableNames = string.Join(", ", companies.Select(c => $"'{c.DisplayName}'"));
            var matchNote = target != null && (target.Id.ToString() == _options.CompanyId || target.DisplayName.Equals(_options.CompanyName, StringComparison.OrdinalIgnoreCase))
                ? "" : $"No exact match for CompanyId='{_options.CompanyId}' or CompanyName='{_options.CompanyName}'. Available: {availableNames}. Using: '{target?.DisplayName}'.";

            return (true, target?.DisplayName ?? "", matchNote);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private async Task<(bool Connected, string Server, string Database, string Error)> CheckSqlConnectionAsync()
    {
        try
        {
            var conn = _db.Database.GetConnectionString() ?? "";
            var connected = await _db.Database.CanConnectAsync();
            var (server, database) = ParseSqlConnectionString(conn);
            return (connected, server, database, "");
        }
        catch (Exception ex)
        {
            return (false, "", "", ex.Message);
        }
    }

    private static (string Server, string Database) ParseSqlConnectionString(string conn)
    {
        var server = "";
        var database = "";
        foreach (var part in conn.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim().ToLowerInvariant();
            if (key is "server" or "data source") server = kv[1].Trim();
            if (key is "database" or "initial catalog") database = kv[1].Trim();
        }
        return (server, database);
    }

    [Function("BcCheckPayments")]
    public async Task<HttpResponseData> CheckPayments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/payments")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();

        var customerBalances = await FetchCustomerBalancesAsync(companyId);
        var journalPayments = await FetchJournalPaymentsAsync(companyId);
        var (paidInvoices, allInvoiceStatuses) = await FetchInvoiceStatusesAsync(companyId);
        var (customerLedgerEntries, ledgerError) = await FetchPaymentLedgerEntriesAsync(companyId);
        var (glPaymentEntries, glError) = await FetchGlPaymentEntriesAsync(companyId);

        return await JsonResponse(req, new
        {
            customerBalances,
            journalPayments,
            paidInvoices,
            allInvoiceStatuses,
            customerLedgerEntries,
            ledgerError,
            glPaymentEntries,
            glError,
            totalCustomers = customerBalances.Count,
            totalJournalPayments = journalPayments.Count,
            totalPaidInvoices = paidInvoices.Count,
            totalCustomerLedgerEntries = customerLedgerEntries.Count,
            totalGlPaymentEntries = glPaymentEntries.Count
        });
    }

    private async Task<List<object>> FetchCustomerBalancesAsync(Guid companyId)
    {
        try
        {
            var customers = await _bcClient!.GetCustomerBalancesAsync(companyId);
            return customers.Select(c => (object)new { c.Number, c.DisplayName, c.Balance, c.OverdueAmount, c.CurrencyCode }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch customers for balance check");
            return new List<object>();
        }
    }

    private async Task<List<object>> FetchJournalPaymentsAsync(Guid companyId)
    {
        var journals = await _bcClient!.GetCustomerPaymentJournalsAsync(companyId);
        var result = new List<object>();
        foreach (var journal in journals)
        {
            var payments = await _bcClient.GetCustomerPaymentsAsync(companyId, journal.Id);
            result.AddRange(payments.Select(p => (object)new
            {
                journal = journal.DisplayName,
                p.CustomerNumber, p.CustomerName, p.PostingDate, p.DocumentNumber,
                p.ExternalDocumentNumber, p.Amount, p.AppliesToInvoiceNumber, p.Description
            }));
        }
        return result;
    }

    private async Task<(List<object> Paid, List<object> All)> FetchInvoiceStatusesAsync(Guid companyId)
    {
        var paid = new List<object>();
        var all = new List<object>();
        try
        {
            var invoices = await _bcClient!.GetSalesInvoicesAsync(companyId, 5000);
            foreach (var inv in invoices)
            {
                var entry = new
                {
                    inv.Number, inv.ExternalDocumentNumber, inv.CustomerNumber, inv.CustomerName,
                    inv.TotalAmountIncludingTax, inv.RemainingAmount, inv.InvoiceDate, inv.Status
                };
                all.Add(entry);
                if (inv.Status == "Paid" || inv.RemainingAmount == 0) paid.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch sales invoices for payment status");
        }
        return (paid, all);
    }

    private async Task<(List<object> Entries, string? Error)> FetchPaymentLedgerEntriesAsync(Guid companyId)
    {
        try
        {
            var entries = await _bcClient!.GetCustomerLedgerEntriesAsync(companyId);
            var result = entries
                .Where(e => e.DocumentType.Equals("Payment", StringComparison.OrdinalIgnoreCase))
                .Select(e => (object)new
                {
                    e.EntryNo, e.PostingDate, e.DocumentNo, e.DocumentType, e.CustomerNo,
                    e.CustomerName, e.Description, e.Amount, e.RemainingAmount, e.Open, e.ExternalDocumentNo
                }).ToList();
            return (result, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch customer ledger entries");
            return (new List<object>(), ex.Message);
        }
    }

    private async Task<(List<object> Entries, string? Error)> FetchGlPaymentEntriesAsync(Guid companyId)
    {
        try
        {
            var glEntries = await _bcClient!.GetGeneralLedgerEntriesAsync(companyId, 1000);
            var result = glEntries
                .Where(e => e.DocumentType.Equals("Payment", StringComparison.OrdinalIgnoreCase))
                .Select(e => (object)new
                {
                    e.EntryNumber, e.PostingDate, e.DocumentNumber, e.DocumentType,
                    e.AccountNumber, e.Description, e.DebitAmount, e.CreditAmount
                }).ToList();
            return (result, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch general ledger entries");
            return (new List<object>(), ex.Message);
        }
    }

    [Function("BcBuyerBalances")]
    public async Task<HttpResponseData> GetBuyerBalances(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/buyer-balances")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();

        // Get all customer ledger entries from BC
        var allEntries = await _bcClient.GetCustomerLedgerEntriesAsync(companyId);

        // Get all buyers from our system to map customer numbers to buyer names
        var buyers = await _db.Buyers.ToListAsync();
        var buyerMap = buyers.ToDictionary(b => b.BuyerNumber, b => b.Name);

        // Group by customer and calculate balances
        var customerGroups = allEntries
            .GroupBy(e => e.CustomerNo)
            .Select(g =>
            {
                var invoiceEntries = g.Where(e => IsDocType(e.DocumentType, "Invoice")).ToList();
                var creditMemoEntries = g.Where(e => IsDocType(e.DocumentType, "Credit Memo")).ToList();
                var paymentEntries = g.Where(e => IsDocType(e.DocumentType, "Payment")).ToList();

                // Open payment remaining is negative — its absolute value is the unallocated amount
                var openPaymentRemaining = paymentEntries.Where(e => e.Open).Sum(e => e.RemainingAmount);
                var totalPayments = paymentEntries.Sum(e => Math.Abs(e.OriginalAmount));
                var totalInvoiced = invoiceEntries.Sum(e => e.OriginalAmount);
                var totalCredited = creditMemoEntries.Sum(e => Math.Abs(e.OriginalAmount));
                var openInvoiceRemaining = invoiceEntries.Where(e => e.Open).Sum(e => e.RemainingAmount);

                return new
                {
                    customerNo = g.Key,
                    customerName = g.First().CustomerName,
                    buyerName = buyerMap.TryGetValue(g.Key, out var name) ? name : null,
                    totalInvoiced,
                    totalCredited,
                    totalPayments,
                    openInvoiceRemaining,
                    unallocatedPayment = Math.Abs(openPaymentRemaining),
                    openInvoiceCount = invoiceEntries.Count(e => e.Open),
                    openPaymentCount = paymentEntries.Count(e => e.Open)
                };
            })
            .Where(c => c.totalPayments > 0 || c.totalInvoiced > 0)
            .OrderBy(c => c.customerNo)
            .ToList();

        // Include distinct document types for diagnostics
        var documentTypes = allEntries.Select(e => e.DocumentType).Distinct().ToList();

        return await JsonResponse(req, new
        {
            buyers = customerGroups,
            lastChecked = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            totalEntries = allEntries.Count,
            documentTypes
        });
    }

    [Function("BcCustomerEntries")]
    public async Task<HttpResponseData> GetCustomerEntries(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/customer-entries/{customerNo}")] HttpRequestData req, string customerNo)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();
        var allEntries = await _bcClient.GetCustomerLedgerEntriesAsync(companyId);
        var customerEntries = allEntries.Where(e => e.CustomerNo == customerNo).ToList();

        return await JsonResponse(req, new
        {
            customerNo,
            totalEntries = customerEntries.Count,
            entries = customerEntries.Select(e => new
            {
                e.EntryNo,
                e.PostingDate,
                e.DocumentType,
                e.DocumentNo,
                e.Description,
                e.OriginalAmount,
                e.RemainingAmount,
                e.Open
            }).OrderByDescending(e => e.PostingDate).ToList()
        });
    }

    private static string? DetectPaymentMismatch(string localStatus, string bcStatus)
    {
        if (localStatus == "Paid" && bcStatus == "Open") return "Marked Paid locally but still Open in BC";
        if (localStatus == "Issued" && bcStatus == "Closed") return "Still Issued locally but already Closed in BC";
        if (localStatus == "Downpayment" && bcStatus == "Closed") return "Partially Paid locally but fully Closed in BC";
        return null;
    }

    private static bool IsDocType(string actual, string expected)
    {
        if (string.IsNullOrEmpty(actual)) return false;
        // Exact match (case insensitive)
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
        // BC encodes spaces as _x0020_ in OData responses (e.g., "Credit_x0020_Memo")
        var decoded = actual.Replace("_x0020_", " ");
        if (decoded.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
        // Also handle without spaces or underscores
        var normalized = decoded.Replace(" ", "").Replace("_", "");
        var expectedNormalized = expected.Replace(" ", "").Replace("_", "");
        return normalized.Equals(expectedNormalized, StringComparison.OrdinalIgnoreCase);
    }

    private bool EnsureConfigured(out object? error)
    {
        if (!_options.IsConfigured || _syncService is null || _bcClient is null)
        {
            error = new { configured = false, message = "Business Central is not configured. Set BC_TENANT_ID, BC_CLIENT_ID, BC_CLIENT_SECRET environment variables." };
            return false;
        }
        error = null;
        return true;
    }

    [Function("BcCustomerStatement")]
    public async Task<HttpResponseData> GetCustomerStatement(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/statement/{customerNo}")] HttpRequestData req, string customerNo)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        try
        {
            var companyId = await _bcClient!.ResolveCompanyIdAsync();

            // Get customer details
            var customers = await _bcClient.GetCustomersAsync(companyId);
            var customer = customers.FirstOrDefault(c => c.Number == customerNo);
            if (customer == null)
                return await JsonResponse(req, new { error = $"Customer {customerNo} not found in BC" }, HttpStatusCode.NotFound);

            // Get ledger entries
            var allEntries = await _bcClient.GetCustomerLedgerEntriesAsync(companyId);
            var customerEntries = allEntries
                .Where(e => e.CustomerNo == customerNo)
                .OrderBy(e => e.PostingDate)
                .ToList();

            if (customerEntries.Count == 0)
                return await JsonResponse(req, new { error = $"No ledger entries found for customer {customerNo}" }, HttpStatusCode.NotFound);

            // Generate PDF
            var generator = new StatementOfAccountGenerator();
            var statementEntries = customerEntries.Select(e => new StatementEntry
            {
                PostingDate = e.PostingDate ?? "",
                DocumentType = e.DocumentType ?? "",
                DocumentNo = e.DocumentNo ?? "",
                Description = e.Description ?? "",
                OriginalAmount = e.OriginalAmount,
                RemainingAmount = e.RemainingAmount,
                Open = e.Open
            }).ToList();

            var address = $"{customer.AddressLine1}";
            if (!string.IsNullOrEmpty(customer.City))
                address += $", {customer.City}";
            if (!string.IsNullOrEmpty(customer.PostalCode))
                address += $" {customer.PostalCode}";
            if (!string.IsNullOrEmpty(customer.Country))
                address += $", {customer.Country}";

            var pdfBytes = generator.Generate(
                customerNo,
                customer.DisplayName ?? customerNo,
                address,
                statementEntries,
                DateTime.UtcNow);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/pdf");
            response.Headers.Add("Content-Disposition", $"inline; filename=\"Statement_{customerNo}_{DateTime.UtcNow:yyyyMMdd}.pdf\"");
            await response.Body.WriteAsync(pdfBytes);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate statement for customer {CustomerNo}", customerNo);
            return await JsonResponse(req, new { error = ex.Message }, HttpStatusCode.InternalServerError);
        }
    }

    [Function("BCPaymentInfoDiagnostic")]
    public async Task<HttpResponseData> BCPaymentInfoDiagnostic(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/payment-info-diagnostic")] HttpRequestData req)
    {
        if (_bcClient == null)
            return await JsonResponse(req, new { error = "BC not configured" });

        try
        {
            var companyId = await _bcClient.ResolveCompanyIdAsync();
            var companyInfo = await _bcClient.GetCompanyInformationRawAsync(companyId);
            var bankAccounts = await _bcClient.GetBankAccountsRawAsync(companyId);
            return await JsonResponse(req, new { companyId, companyInformation = companyInfo, bankAccounts });
        }
        catch (Exception ex)
        {
            return await JsonResponse(req, new { error = ex.Message }, HttpStatusCode.InternalServerError);
        }
    }

    [Function("BCItemsDiagnostic")]
    public async Task<HttpResponseData> BCItemsDiagnostic(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/items-diagnostic")] HttpRequestData req)
    {
        if (_bcClient == null)
            return await JsonResponse(req, new { error = "BC not configured" });

        try
        {
            var companyId = await _bcClient.ResolveCompanyIdAsync();
            var items = await _bcClient.GetItemsAsync(companyId);
            var serviceItems = items.Where(i => i.Type == "Service" || i.Type == "Non-Inventory").Select(i => new { i.Number, i.DisplayName, i.Type }).ToList();
            var existing = items.Select(i => i.Number).Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // The ACTUAL item the push uses per parameter, and whether it exists in this company.
            // (No "hardcoded" values — the push resolves these from the BcItem_* parameters.)
            var keys = new[] { "BcItem_LotSale", "BcItem_AuctionFee", "BcItem_Commission" };
            var map = await _db.SystemParameters.Where(p => keys.Contains(p.Key)).ToDictionaryAsync(p => p.Key, p => p.Value);
            object Resolved(string key)
            {
                map.TryGetValue(key, out var v);
                var num = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                return new { parameter = key, configured = num, existsInBc = num != null && existing.Contains(num) };
            }
            return await JsonResponse(req, new
            {
                companyId,
                configuredItems = new[] { Resolved("BcItem_LotSale"), Resolved("BcItem_AuctionFee"), Resolved("BcItem_Commission") },
                allServiceItems = serviceItems,
                allItemCount = items.Count
            });
        }
        catch (Exception ex)
        {
            return await JsonResponse(req, new { error = ex.Message }, HttpStatusCode.InternalServerError);
        }
    }

    [Function("BCTestPdfGeneration")]
    public async Task<HttpResponseData> BCTestPdfGeneration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/test-pdf-gen")] HttpRequestData req)
    {
        if (_bcClient == null)
            return await JsonResponse(req, new { error = "BC not configured" });

        var invoiceNo = req.Query["invoiceNo"];
        var docType = req.Query["docType"] ?? "Sales Invoice";
        if (string.IsNullOrEmpty(invoiceNo))
            return await JsonResponse(req, new { error = "Pass ?invoiceNo=BD00020" }, HttpStatusCode.BadRequest);

        try
        {
            var companyId = await _bcClient.ResolveCompanyIdAsync();

            // Step 1: Test standard pdfDocument endpoint
            string? standardError = null;
            int standardPdfSize = 0;
            try
            {
                var inv = await _bcClient.GetSalesInvoiceByExternalDocAsync(companyId, invoiceNo);
                if (inv != null)
                {
                    var pdfBytes = await _bcClient.GetSalesInvoicePdfAsync(companyId, inv.Id);
                    standardPdfSize = pdfBytes?.Length ?? 0;
                    if (standardPdfSize == 0) standardError = "PDF returned empty/null";
                }
                else
                {
                    standardError = $"No posted invoice found with number or externalDocNo '{invoiceNo}'";
                }
            }
            catch (Exception ex)
            {
                standardError = ex.Message;
            }

            // Step 2: Test custom extension endpoint
            string? customError = null;
            bool customSuccess = false;
            int customReportId = 0;
            try
            {
                var (success, error, reportId) = await _bcClient.RequestPdfGenerationAsync(companyId, invoiceNo, docType);
                customSuccess = success;
                customError = error;
                customReportId = reportId;
            }
            catch (Exception ex)
            {
                customError = $"Exception: {ex.Message}";
            }

            return await JsonResponse(req, new
            {
                invoiceNo,
                docType,
                standardEndpoint = new { pdfSizeBytes = standardPdfSize, error = standardError },
                customEndpoint = new { success = customSuccess, reportIdUsed = customReportId, error = customError }
            });
        }
        catch (Exception ex)
        {
            return await JsonResponse(req, new { error = ex.Message }, HttpStatusCode.InternalServerError);
        }
    }

    private static async Task<HttpResponseData> JsonResponse(HttpRequestData req, object data, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return response;
    }
}
