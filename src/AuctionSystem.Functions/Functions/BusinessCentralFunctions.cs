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

        var results = new List<object>();

        foreach (var invoice in invoices)
        {
            var bcInvNo = invoice.BcInvoiceNumber!;
            var localStatus = invoice.Status.ToString();
            var buyerNo = invoice.Buyer?.BuyerNumber ?? "";

            // Check BC ledger for this invoice
            var bcEntry = invoiceLedgerEntries.GetValueOrDefault(bcInvNo);
            var bcStatus = bcEntry == null ? "Not Found" : (bcEntry.Open ? "Open" : "Closed");
            var bcRemaining = bcEntry?.RemainingAmount ?? 0;

            // Check for open payments on this customer
            var openPayments = paymentLedgerEntries.GetValueOrDefault(buyerNo) ?? new List<BcCustomerLedgerEntry>();
            var totalOpenPayment = openPayments.Sum(p => Math.Abs(p.RemainingAmount));

            // Determine mismatch
            string? issue = null;
            if (localStatus == "Paid" && bcStatus == "Open")
                issue = "Marked Paid locally but still Open in BC";
            else if (localStatus == "Issued" && bcStatus == "Closed")
                issue = "Still Issued locally but already Closed in BC";
            else if (localStatus == "Downpayment" && bcStatus == "Closed")
                issue = "Partial Payment locally but fully Closed in BC";

            results.Add(new
            {
                invoiceId = invoice.Id,
                bcInvoiceNumber = bcInvNo,
                buyerNumber = buyerNo,
                buyerName = invoice.Buyer?.Name ?? "",
                localStatus,
                bcStatus,
                invoiceAmount = invoice.TotalAmount,
                bcRemainingAmount = bcRemaining,
                openPaymentAvailable = totalOpenPayment,
                issue
            });
        }

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
        var bcConnected = false;
        var bcCompanyName = "";
        var bcError = "";
        if (_options.IsConfigured && _bcClient is not null)
        {
            try
            {
                var companies = await _bcClient.GetCompaniesAsync();
                bcConnected = companies.Count > 0;
                var target = companies.FirstOrDefault(c => c.Id.ToString() == _options.CompanyId);
                bcCompanyName = target?.DisplayName ?? companies.FirstOrDefault()?.DisplayName ?? "";
            }
            catch (Exception ex)
            {
                bcError = ex.Message;
            }
        }

        var sqlConnected = false;
        var sqlServer = "";
        var sqlDatabase = "";
        var sqlError = "";
        try
        {
            var conn = _db.Database.GetConnectionString() ?? "";
            sqlConnected = await _db.Database.CanConnectAsync();
            var parts = conn.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2)
                {
                    var key = kv[0].Trim().ToLowerInvariant();
                    if (key is "server" or "data source") sqlServer = kv[1].Trim();
                    if (key is "database" or "initial catalog") sqlDatabase = kv[1].Trim();
                }
            }
        }
        catch (Exception ex)
        {
            sqlError = ex.Message;
        }

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
                sql = new
                {
                    connected = sqlConnected,
                    server = sqlServer,
                    database = sqlDatabase,
                    error = sqlError
                },
                blobStorage = new
                {
                    configured = _blobStorage is not null
                }
            }
        });
    }

    [Function("BcCheckPayments")]
    public async Task<HttpResponseData> CheckPayments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bc/payments")] HttpRequestData req)
    {
        if (!EnsureConfigured(out var error))
            return await JsonResponse(req, error!, HttpStatusCode.BadRequest);

        var companyId = await _bcClient!.ResolveCompanyIdAsync();

        // 1. Get customer balances from standard API — shows outstanding amounts
        var customerBalances = new List<object>();
        try
        {
            var customers = await _bcClient.GetCustomerBalancesAsync(companyId);
            foreach (var c in customers)
            {
                customerBalances.Add(new
                {
                    c.Number,
                    c.DisplayName,
                    c.Balance,
                    c.OverdueAmount,
                    c.CurrencyCode
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch customers for balance check");
        }

        // 2. Get customer payment journals (unposted draft payments)
        var journals = await _bcClient.GetCustomerPaymentJournalsAsync(companyId);
        var journalPayments = new List<object>();
        foreach (var journal in journals)
        {
            var payments = await _bcClient.GetCustomerPaymentsAsync(companyId, journal.Id);
            foreach (var p in payments)
            {
                journalPayments.Add(new
                {
                    journal = journal.DisplayName,
                    p.CustomerNumber,
                    p.CustomerName,
                    p.PostingDate,
                    p.DocumentNumber,
                    p.ExternalDocumentNumber,
                    p.Amount,
                    p.AppliesToInvoiceNumber,
                    p.Description
                });
            }
        }

        // 3. Check posted sales invoices for payment status
        var paidInvoices = new List<object>();
        var allInvoiceStatuses = new List<object>();
        try
        {
            var invoices = await _bcClient.GetSalesInvoicesAsync(companyId, 5000);
            foreach (var inv in invoices)
            {
                allInvoiceStatuses.Add(new
                {
                    inv.Number,
                    inv.ExternalDocumentNumber,
                    inv.CustomerNumber,
                    inv.CustomerName,
                    inv.TotalAmountIncludingTax,
                    inv.RemainingAmount,
                    inv.InvoiceDate,
                    inv.Status
                });

                if (inv.Status == "Paid" || inv.RemainingAmount == 0)
                {
                    paidInvoices.Add(new
                    {
                        inv.Number,
                        inv.ExternalDocumentNumber,
                        inv.CustomerNumber,
                        inv.CustomerName,
                        inv.TotalAmountIncludingTax,
                        inv.RemainingAmount,
                        inv.InvoiceDate,
                        inv.Status
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch sales invoices for payment status");
        }

        // 4. Get customer ledger entries via custom API (payments applied to customers)
        var customerLedgerEntries = new List<object>();
        string? ledgerError = null;
        try
        {
            var entries = await _bcClient.GetCustomerLedgerEntriesAsync(companyId);
            foreach (var e in entries.Where(e =>
                e.DocumentType.Equals("Payment", StringComparison.OrdinalIgnoreCase)))
            {
                customerLedgerEntries.Add(new
                {
                    e.EntryNo,
                    e.PostingDate,
                    e.DocumentNo,
                    e.DocumentType,
                    e.CustomerNo,
                    e.CustomerName,
                    e.Description,
                    e.Amount,
                    e.RemainingAmount,
                    e.Open,
                    e.ExternalDocumentNo
                });
            }
        }
        catch (Exception ex)
        {
            ledgerError = ex.Message;
            _logger.LogWarning(ex, "Could not fetch customer ledger entries");
        }

        // 5. Get general ledger entries (fallback for payment postings)
        var glPaymentEntries = new List<object>();
        string? glError = null;
        try
        {
            var glEntries = await _bcClient.GetGeneralLedgerEntriesAsync(companyId, 1000);
            foreach (var e in glEntries.Where(e =>
                e.DocumentType.Equals("Payment", StringComparison.OrdinalIgnoreCase)))
            {
                glPaymentEntries.Add(new
                {
                    e.EntryNumber,
                    e.PostingDate,
                    e.DocumentNumber,
                    e.DocumentType,
                    e.AccountNumber,
                    e.Description,
                    e.DebitAmount,
                    e.CreditAmount
                });
            }
        }
        catch (Exception ex)
        {
            glError = ex.Message;
            _logger.LogWarning(ex, "Could not fetch general ledger entries");
        }

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

    private static async Task<HttpResponseData> JsonResponse(HttpRequestData req, object data, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return response;
    }
}
