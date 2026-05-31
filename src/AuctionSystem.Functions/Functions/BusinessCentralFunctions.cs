using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Functions.BusinessCentral.Configuration;
using AuctionSystem.Functions.BusinessCentral.Services;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

public class BusinessCentralFunctions
{
    private readonly BusinessCentralSyncService? _syncService;
    private readonly BusinessCentralApiClient? _bcClient;
    private readonly BusinessCentralOptions _options;
    private readonly AuctionDbContext _db;
    private readonly ILogger<BusinessCentralFunctions> _logger;

    public BusinessCentralFunctions(
        IOptions<BusinessCentralOptions> options,
        AuctionDbContext db,
        ILogger<BusinessCentralFunctions> logger,
        BusinessCentralSyncService? syncService = null,
        BusinessCentralApiClient? bcClient = null)
    {
        _options = options.Value;
        _db = db;
        _logger = logger;
        _syncService = syncService;
        _bcClient = bcClient;
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

    [Function("BcConsistencyCheck")]
    public async Task<HttpResponseData> ConsistencyCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/consistency-check")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var err))
            return await JsonResponse(req, err!, HttpStatusCode.ServiceUnavailable);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();
        var mismatches = new List<object>();

        // Check Brokers ↔ BC Vendors
        var brokers = await _db.Brokers.Where(b => b.IsActive).ToListAsync();
        foreach (var broker in brokers)
        {
            var bcVendor = await _bcClient.GetVendorByNumberAsync(companyId, broker.BrokerNumber);
            if (bcVendor is null)
            {
                mismatches.Add(new { entity = "Broker", number = broker.BrokerNumber, name = broker.CompanyName, field = "BC Vendor", auction = "exists", bc = "NOT FOUND" });
                continue;
            }

            var localCurrency = broker.Currency == "EUR" ? "" : broker.Currency;
            if (!string.IsNullOrEmpty(broker.GenBusPostingGroup) && broker.GenBusPostingGroup != bcVendor.GenBusPostingGroup)
                mismatches.Add(new { entity = "Broker", number = broker.BrokerNumber, name = broker.CompanyName, field = "GenBusPostingGroup", auction = broker.GenBusPostingGroup, bc = bcVendor.GenBusPostingGroup });
            if (!string.IsNullOrEmpty(broker.VatBusPostingGroup) && broker.VatBusPostingGroup != bcVendor.VatBusPostingGroup)
                mismatches.Add(new { entity = "Broker", number = broker.BrokerNumber, name = broker.CompanyName, field = "VatBusPostingGroup", auction = broker.VatBusPostingGroup, bc = bcVendor.VatBusPostingGroup });
            if (!string.IsNullOrEmpty(localCurrency) && localCurrency != bcVendor.CurrencyCode)
                mismatches.Add(new { entity = "Broker", number = broker.BrokerNumber, name = broker.CompanyName, field = "CurrencyCode", auction = broker.Currency, bc = string.IsNullOrEmpty(bcVendor.CurrencyCode) ? "(LCY)" : bcVendor.CurrencyCode });
            if (!string.IsNullOrEmpty(broker.Country) && broker.Country != bcVendor.Country)
                mismatches.Add(new { entity = "Broker", number = broker.BrokerNumber, name = broker.CompanyName, field = "Country", auction = broker.Country, bc = bcVendor.Country });
        }

        // Check Buyers ↔ BC Customers
        var buyers = await _db.Buyers.Where(b => b.IsActive).ToListAsync();
        foreach (var buyer in buyers)
        {
            var bcCustomer = await _bcClient.GetCustomerByNumberAsync(companyId, buyer.BuyerNumber);
            if (bcCustomer is null)
            {
                mismatches.Add(new { entity = "Buyer", number = buyer.BuyerNumber, name = buyer.Name, field = "BC Customer", auction = "exists", bc = "NOT FOUND" });
                continue;
            }

            var localCurrency = buyer.Currency == "EUR" ? "" : buyer.Currency;
            if (!string.IsNullOrEmpty(buyer.GenBusPostingGroup) && buyer.GenBusPostingGroup != bcCustomer.GenBusPostingGroup)
                mismatches.Add(new { entity = "Buyer", number = buyer.BuyerNumber, name = buyer.Name, field = "GenBusPostingGroup", auction = buyer.GenBusPostingGroup, bc = bcCustomer.GenBusPostingGroup });
            if (!string.IsNullOrEmpty(buyer.VatBusPostingGroup) && buyer.VatBusPostingGroup != bcCustomer.VatBusPostingGroup)
                mismatches.Add(new { entity = "Buyer", number = buyer.BuyerNumber, name = buyer.Name, field = "VatBusPostingGroup", auction = buyer.VatBusPostingGroup, bc = bcCustomer.VatBusPostingGroup });
            if (!string.IsNullOrEmpty(buyer.CustomerPostingGroup) && buyer.CustomerPostingGroup != bcCustomer.CustomerPostingGroup)
                mismatches.Add(new { entity = "Buyer", number = buyer.BuyerNumber, name = buyer.Name, field = "CustomerPostingGroup", auction = buyer.CustomerPostingGroup, bc = bcCustomer.CustomerPostingGroup });
            if (!string.IsNullOrEmpty(localCurrency) && localCurrency != bcCustomer.CurrencyCode)
                mismatches.Add(new { entity = "Buyer", number = buyer.BuyerNumber, name = buyer.Name, field = "CurrencyCode", auction = buyer.Currency, bc = string.IsNullOrEmpty(bcCustomer.CurrencyCode) ? "(LCY)" : bcCustomer.CurrencyCode });
            if (!string.IsNullOrEmpty(buyer.Country) && buyer.Country != bcCustomer.Country)
                mismatches.Add(new { entity = "Buyer", number = buyer.BuyerNumber, name = buyer.Name, field = "Country", auction = buyer.Country, bc = bcCustomer.Country });
        }

        // Check Farmers ↔ BC Vendors
        var farmers = await _db.Farmers.Where(f => f.IsActive).ToListAsync();
        foreach (var farmer in farmers)
        {
            var bcVendor = await _bcClient.GetVendorByNumberAsync(companyId, farmer.FarmerNumber);
            if (bcVendor is null)
            {
                mismatches.Add(new { entity = "Farmer", number = farmer.FarmerNumber, name = farmer.Name, field = "BC Vendor", auction = "exists", bc = "NOT FOUND" });
                continue;
            }

            var localCurrency = farmer.Currency == "EUR" ? "" : farmer.Currency;
            if (!string.IsNullOrEmpty(farmer.GenBusPostingGroup) && farmer.GenBusPostingGroup != bcVendor.GenBusPostingGroup)
                mismatches.Add(new { entity = "Farmer", number = farmer.FarmerNumber, name = farmer.Name, field = "GenBusPostingGroup", auction = farmer.GenBusPostingGroup, bc = bcVendor.GenBusPostingGroup });
            if (!string.IsNullOrEmpty(farmer.VatBusPostingGroup) && farmer.VatBusPostingGroup != bcVendor.VatBusPostingGroup)
                mismatches.Add(new { entity = "Farmer", number = farmer.FarmerNumber, name = farmer.Name, field = "VatBusPostingGroup", auction = farmer.VatBusPostingGroup, bc = bcVendor.VatBusPostingGroup });
            if (!string.IsNullOrEmpty(farmer.VendorPostingGroup) && farmer.VendorPostingGroup != bcVendor.VendorPostingGroup)
                mismatches.Add(new { entity = "Farmer", number = farmer.FarmerNumber, name = farmer.Name, field = "VendorPostingGroup", auction = farmer.VendorPostingGroup, bc = bcVendor.VendorPostingGroup });
            if (!string.IsNullOrEmpty(localCurrency) && localCurrency != bcVendor.CurrencyCode)
                mismatches.Add(new { entity = "Farmer", number = farmer.FarmerNumber, name = farmer.Name, field = "CurrencyCode", auction = farmer.Currency, bc = string.IsNullOrEmpty(bcVendor.CurrencyCode) ? "(LCY)" : bcVendor.CurrencyCode });
            if (!string.IsNullOrEmpty(farmer.Country) && farmer.Country != bcVendor.Country)
                mismatches.Add(new { entity = "Farmer", number = farmer.FarmerNumber, name = farmer.Name, field = "Country", auction = farmer.Country, bc = bcVendor.Country });
        }

        return await JsonResponse(req, new
        {
            message = mismatches.Count == 0 ? "All consistent" : $"{mismatches.Count} mismatch(es) found",
            checked_ = new { brokers = brokers.Count, buyers = buyers.Count, farmers = farmers.Count },
            mismatches
        });
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

    private static async Task<HttpResponseData> JsonResponse(HttpRequestData req, object data, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return response;
    }
}
