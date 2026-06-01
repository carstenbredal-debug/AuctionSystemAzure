using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AuctionSystem.Functions.BusinessCentral.Configuration;
using AuctionSystem.Functions.BusinessCentral.Models;

namespace AuctionSystem.Functions.BusinessCentral.Services;

public class BusinessCentralApiClient
{
    private readonly HttpClient _httpClient;
    private readonly BusinessCentralAuthService _authService;
    private readonly BusinessCentralOptions _options;
    private readonly ILogger<BusinessCentralApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BusinessCentralApiClient(
        HttpClient httpClient,
        BusinessCentralAuthService authService,
        IOptions<BusinessCentralOptions> options,
        ILogger<BusinessCentralApiClient> logger)
    {
        _httpClient = httpClient;
        _authService = authService;
        _options = options.Value;
        _logger = logger;
    }

    // ── Companies ──────────────────────────────────────────────

    public async Task<List<BcCompany>> GetCompaniesAsync()
    {
        var url = $"{_options.BaseUrl}/companies";
        return await GetListAsync<BcCompany>(url);
    }

    public async Task<Guid> ResolveCompanyIdAsync()
    {
        if (Guid.TryParse(_options.CompanyId, out var configured))
            return configured;

        var companies = await GetCompaniesAsync();
        if (companies.Count == 0)
            throw new InvalidOperationException("No companies found in Business Central.");

        // Match by name if configured
        if (!string.IsNullOrEmpty(_options.CompanyName))
        {
            var match = companies.FirstOrDefault(c =>
                string.Equals(c.DisplayName, _options.CompanyName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, _options.CompanyName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                _logger.LogInformation("Using company '{Name}' ({Id}) matched by name", match.DisplayName, match.Id);
                return match.Id;
            }
            _logger.LogWarning("Company name '{Name}' not found, falling back to first company", _options.CompanyName);
        }

        var company = companies[0];
        _logger.LogInformation("Using company '{Name}' ({Id})", company.DisplayName, company.Id);
        return company.Id;
    }

    // ── Customers (using custom Auction System API for posting groups) ──

    public async Task<List<BcCustomer>> GetCustomersAsync(Guid companyId, int top = 1000)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/customers?$top={top}";
        return await GetListAsync<BcCustomer>(url);
    }

    public async Task<BcCustomer?> GetCustomerAsync(Guid companyId, Guid customerId)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/customers({customerId})";
        return await GetSingleAsync<BcCustomer>(url);
    }

    public async Task<BcCustomer?> GetCustomerByNumberAsync(Guid companyId, string number)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/customers?$filter=number eq '{number}'";
        var items = await GetListAsync<BcCustomer>(url);
        return items.FirstOrDefault();
    }

    public async Task<BcCustomer> CreateCustomerAsync(Guid companyId, BcCustomer customer)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/customers";
        return await PostAsync<BcCustomer>(url, customer);
    }

    public async Task<BcCustomer> UpdateCustomerAsync(Guid companyId, BcCustomer customer)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/customers({customer.Id})";
        return await PatchAsync<BcCustomer>(url, customer, customer.ETag);
    }

    // ── Vendors ──────────────────────────────────────────────────

    public async Task<List<BcVendor>> GetVendorsAsync(Guid companyId, int top = 1000)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/vendors?$top={top}";
        return await GetListAsync<BcVendor>(url);
    }

    public async Task<BcVendor?> GetVendorByNumberAsync(Guid companyId, string number)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/vendors?$filter=number eq '{number}'";
        var items = await GetListAsync<BcVendor>(url);
        return items.FirstOrDefault();
    }

    public async Task<BcVendor> CreateVendorAsync(Guid companyId, BcVendor vendor)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/vendors";
        return await PostAsync<BcVendor>(url, vendor);
    }

    public async Task<BcVendor> UpdateVendorAsync(Guid companyId, BcVendor vendor)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/vendors({vendor.Id})";
        return await PatchAsync<BcVendor>(url, vendor, vendor.ETag);
    }

    public async Task<BcVendor?> GetStandardVendorByIdAsync(Guid companyId, Guid vendorId)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/vendors({vendorId})";
        return await GetSingleAsync<BcVendor>(url);
    }

    // ── Vendor Posting Groups ────────────────────────

    public async Task<List<BcPostingGroup>> GetVendorPostingGroupsAsync(Guid companyId, string companyName)
    {
        // Try custom API first (page 50102), fall back to OData web service
        try
        {
            var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/vendorPostingGroups";
            return await GetListAsync<BcPostingGroup>(url);
        }
        catch
        {
            try
            {
                var url = $"{ODataBaseUrl}/Company('{Uri.EscapeDataString(companyName)}')/VendorPostingGroups";
                return await GetListAsync<BcPostingGroup>(url);
            }
            catch
            {
                _logger.LogWarning("Could not fetch vendor posting groups from custom API or OData");
                return new List<BcPostingGroup>();
            }
        }
    }

    public async Task PatchVendorTaxRegistrationAsync(Guid companyId, Guid vendorId, string vatRegNo, string etag)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/vendors({vendorId})";
        _logger.LogInformation("PATCH taxRegistrationNumber on {Url}", url);

        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(new { taxRegistrationNumber = vatRegNo }, options: JsonOptions)
        };
        if (!string.IsNullOrEmpty(etag))
            request.Headers.Add("If-Match", etag);

        var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);
    }

    // ── Default Dimensions ────────────────────────────────────

    public async Task SetDefaultDimensionAsync(Guid companyId, Guid parentId, Guid dimensionId, Guid dimensionValueId)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/defaultDimensions";
        _logger.LogInformation("POST defaultDimension on {Url}", url);

        var payload = new
        {
            parentId = parentId,
            dimensionId = dimensionId,
            dimensionValueId = dimensionValueId,
            postingValidation = "Same Code"
        };

        var response = await _httpClient.PostAsJsonAsync(url, payload, JsonOptions);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (body.Contains("EntityWithSameKeyExists"))
            {
                _logger.LogInformation("Default dimension already exists, skipping");
                return;
            }
        }
        await EnsureSuccessAsync(response);
    }

    // ── Countries/Regions ─────────────────────────────────────

    public async Task<List<BcCountryRegion>> GetCountriesRegionsAsync(Guid companyId)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/countriesRegions?$top=500";
        return await GetListAsync<BcCountryRegion>(url);
    }

    // ── Currencies ──────────────────────────────────────────────

    public async Task<List<BcCurrency>> GetCurrenciesAsync(Guid companyId)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/currencies?$top=500";
        return await GetListAsync<BcCurrency>(url);
    }

    // ── Payment Terms ───────────────────────────────────────────

    public async Task<List<BcPaymentTerm>> GetPaymentTermsAsync(Guid companyId)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/paymentTerms?$top=500";
        return await GetListAsync<BcPaymentTerm>(url);
    }

    // ── Posting Groups (OData v4 web services) ──────────────────

    private string ODataBaseUrl =>
        $"https://api.businesscentral.dynamics.com/v2.0/{_options.TenantId}/{_options.Environment}/ODataV4";

    public async Task<List<BcPostingGroup>> GetGenBusinessPostingGroupsAsync(string companyName)
    {
        var url = $"{ODataBaseUrl}/Company('{Uri.EscapeDataString(companyName)}')/GenBusinessPostingGroups";
        return await GetListAsync<BcPostingGroup>(url);
    }

    public async Task<List<BcPostingGroup>> GetVatBusinessPostingGroupsAsync(string companyName)
    {
        var url = $"{ODataBaseUrl}/Company('{Uri.EscapeDataString(companyName)}')/VATBusinessPostingGroups";
        return await GetListAsync<BcPostingGroup>(url);
    }

    public async Task<List<BcPostingGroup>> GetCustomerPostingGroupsAsync(string companyName)
    {
        var url = $"{ODataBaseUrl}/Company('{Uri.EscapeDataString(companyName)}')/CustomerPostingGroups";
        return await GetListAsync<BcPostingGroup>(url);
    }

    // ── Items ──────────────────────────────────────────────────

    public async Task<List<BcItem>> GetItemsAsync(Guid companyId, int top = 1000)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/items?$top={top}";
        return await GetListAsync<BcItem>(url);
    }

    public async Task<BcItem?> GetItemByNumberAsync(Guid companyId, string number)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/items?$filter=number eq '{number}'";
        var items = await GetListAsync<BcItem>(url);
        return items.FirstOrDefault();
    }

    public async Task<BcItem> CreateItemAsync(Guid companyId, BcItem item)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/items";
        return await PostAsync<BcItem>(url, item);
    }

    public async Task<BcItem> UpdateItemAsync(Guid companyId, BcItem item)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/items({item.Id})";
        return await PatchAsync<BcItem>(url, item, item.ETag);
    }

    // ── Sales Invoices ─────────────────────────────────────────

    public async Task<List<BcSalesInvoice>> GetSalesInvoicesAsync(Guid companyId, int top = 1000)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/salesInvoices?$top={top}";
        return await GetListAsync<BcSalesInvoice>(url);
    }

    public async Task<BcSalesInvoice?> GetSalesInvoiceByExternalDocAsync(Guid companyId, string externalDocNumber)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/salesInvoices?$filter=externalDocumentNumber eq '{externalDocNumber}'";
        var invoices = await GetListAsync<BcSalesInvoice>(url);
        return invoices.FirstOrDefault();
    }

    public async Task<BcSalesInvoice> CreateSalesInvoiceAsync(Guid companyId, BcSalesInvoice invoice)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/salesInvoices";
        return await PostAsync<BcSalesInvoice>(url, invoice);
    }

    public async Task<BcSalesInvoiceLine> CreateSalesInvoiceLineAsync(Guid companyId, Guid invoiceId, BcSalesInvoiceLine line)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/salesInvoices({invoiceId})/salesInvoiceLines";
        return await PostAsync<BcSalesInvoiceLine>(url, line);
    }

    public async Task<BcSalesInvoice?> PostSalesInvoiceAsync(Guid companyId, Guid invoiceId)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/salesInvoices({invoiceId})/Microsoft.NAV.post";
        _logger.LogInformation("POST {Url}", url);

        var response = await _httpClient.PostAsync(url, null);
        await EnsureSuccessAsync(response);

        // After posting, the invoice may have a new number from the Posted Invoice Nos. series.
        // Re-fetch it by ID (BC keeps the same ID but status changes to posted).
        try
        {
            var fetchUrl = $"{_options.BaseUrl}/companies({companyId})/salesInvoices({invoiceId})";
            var posted = await GetSingleAsync<BcSalesInvoice>(fetchUrl);
            return posted;
        }
        catch
        {
            return null;
        }
    }

    public async Task DeleteSalesInvoiceAsync(Guid companyId, Guid invoiceId, string? etag = null)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/salesInvoices({invoiceId})";
        _logger.LogInformation("DELETE {Url}", url);

        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Add("If-Match", etag ?? "*");
        var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);
    }

    public async Task<byte[]?> GetSalesInvoicePdfAsync(Guid companyId, Guid invoiceId)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/salesInvoices({invoiceId})/pdfDocument/pdfDocumentContent";
        _logger.LogInformation("GET {Url} (PDF)", url);

        var response = await _httpClient.GetAsync(url);

        // BC may return non-success status (e.g. 501) but still include the PDF in the body.
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
        {
            if (!response.IsSuccessStatusCode)
                _logger.LogInformation("BC returned {Status} for PDF but body contains valid PDF ({Bytes} bytes)", (int)response.StatusCode, bytes.Length);
            return bytes;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = System.Text.Encoding.UTF8.GetString(bytes);
            _logger.LogWarning("Could not download invoice PDF: {Status} {Body}", (int)response.StatusCode, body);
            return null;
        }

        if (bytes.Length == 0)
        {
            _logger.LogWarning("PDF response was empty for invoice {InvoiceId}", invoiceId);
            return null;
        }

        return bytes;
    }

    // ── Customer Payments ─────────────────────────────────────

    public async Task<List<BcCustomerPayment>> GetCustomerPaymentsAsync(Guid companyId, Guid journalId)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/customerPaymentJournals({journalId})/customerPayments?$top=5000";
        return await GetListAsync<BcCustomerPayment>(url);
    }

    public async Task<List<BcCustomerPaymentJournal>> GetCustomerPaymentJournalsAsync(Guid companyId)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/customerPaymentJournals";
        return await GetListAsync<BcCustomerPaymentJournal>(url);
    }

    public async Task<List<BcCustomerLedgerEntry>> GetCustomerLedgerEntriesAsync(Guid companyId)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/generalLedgerEntries?$filter=sourceType eq 'Customer'&$top=5000&$orderby=postingDate desc";
        return await GetListAsync<BcCustomerLedgerEntry>(url);
    }

    // ── Sales Credit Memos ─────────────────────────────────────

    public async Task<BcSalesCreditMemo> CreateSalesCreditMemoAsync(Guid companyId, BcSalesCreditMemo creditMemo)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/salesCreditMemos";
        return await PostAsync<BcSalesCreditMemo>(url, creditMemo);
    }

    public async Task<BcSalesCreditMemoLine> CreateSalesCreditMemoLineAsync(Guid companyId, Guid creditMemoId, BcSalesCreditMemoLine line)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/salesCreditMemos({creditMemoId})/salesCreditMemoLines";
        return await PostAsync<BcSalesCreditMemoLine>(url, line);
    }

    public async Task<BcSalesCreditMemo?> PostSalesCreditMemoAsync(Guid companyId, Guid creditMemoId)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/salesCreditMemos({creditMemoId})/Microsoft.NAV.post";
        _logger.LogInformation("POST {Url}", url);

        var response = await _httpClient.PostAsync(url, null);
        await EnsureSuccessAsync(response);

        try
        {
            var fetchUrl = $"{_options.BaseUrl}/companies({companyId})/salesCreditMemos({creditMemoId})";
            var posted = await GetSingleAsync<BcSalesCreditMemo>(fetchUrl);
            return posted;
        }
        catch
        {
            return null;
        }
    }

    public async Task<BcSalesCreditMemo?> GetSalesCreditMemoByExternalDocAsync(Guid companyId, string externalDocNumber)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/salesCreditMemos?$filter=externalDocumentNumber eq '{externalDocNumber}'";
        var items = await GetListAsync<BcSalesCreditMemo>(url);
        return items.FirstOrDefault();
    }

    public async Task<byte[]?> GetSalesCreditMemoPdfAsync(Guid companyId, Guid creditMemoId)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/salesCreditMemos({creditMemoId})/pdfDocument/pdfDocumentContent";
        _logger.LogInformation("GET {Url} (PDF)", url);

        var response = await _httpClient.GetAsync(url);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
        {
            if (!response.IsSuccessStatusCode)
                _logger.LogInformation("BC returned {Status} for credit memo PDF but body contains valid PDF ({Bytes} bytes)", (int)response.StatusCode, bytes.Length);
            return bytes;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = System.Text.Encoding.UTF8.GetString(bytes);
            _logger.LogWarning("Could not download credit memo PDF: {Status} {Body}", (int)response.StatusCode, body);
            return null;
        }

        if (bytes.Length == 0)
        {
            _logger.LogWarning("PDF response was empty for credit memo {CreditMemoId}", creditMemoId);
            return null;
        }

        return bytes;
    }

    // ── HTTP helpers ───────────────────────────────────────────

    private async Task SetAuthHeaderAsync()
    {
        var token = await _authService.GetAccessTokenAsync();
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<List<T>> GetListAsync<T>(string url)
    {
        await SetAuthHeaderAsync();
        _logger.LogInformation("GET {Url}", url);

        var response = await _httpClient.GetAsync(url);
        await EnsureSuccessAsync(response);

        var odata = await response.Content.ReadFromJsonAsync<ODataResponse<T>>(JsonOptions);
        return odata?.Value ?? new List<T>();
    }

    private async Task<T?> GetSingleAsync<T>(string url) where T : class
    {
        await SetAuthHeaderAsync();
        _logger.LogInformation("GET {Url}", url);

        var response = await _httpClient.GetAsync(url);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    private async Task<T> PostAsync<T>(string url, T payload)
    {
        await SetAuthHeaderAsync();
        _logger.LogInformation("POST {Url}", url);

        var response = await _httpClient.PostAsJsonAsync(url, payload, JsonOptions);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private async Task<T> PatchAsync<T>(string url, T payload, string? etag)
    {
        await SetAuthHeaderAsync();
        _logger.LogInformation("PATCH {Url}", url);

        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };

        if (!string.IsNullOrEmpty(etag))
            request.Headers.Add("If-Match", etag);

        var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("BC API error {Status}: {Body}", (int)response.StatusCode, body);
            throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). BC says: {body}");
        }
    }
}
