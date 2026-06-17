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

    public async Task<string> GetRawODataAsync(string companyName, string entitySet)
    {
        await SetAuthHeaderAsync();
        var url = $"{ODataBaseUrl}/Company('{Uri.EscapeDataString(companyName)}')/{entitySet}";
        _logger.LogInformation("RAW OData GET {Url}", url);
        var response = await _httpClient.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        return $"URL: {url} | Status: {(int)response.StatusCode} | Body: {body}";
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

    public async Task<BcItem?> GetItemByDisplayNameAsync(Guid companyId, string displayName)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/items?$filter=displayName eq '{displayName}'";
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

    /// <summary>Posted sales invoice matching an external document number (used for idempotency on retry).</summary>
    public async Task<BcSalesInvoice?> GetPostedSalesInvoiceByExternalDocAsync(Guid companyId, string externalDocNumber)
    {
        // Standard api/v2.0 has no `postedSalesInvoices` resource (it 404s, and a recreated company
        // won't expose a custom one). Posted invoices stay in `salesInvoices` with a non-Draft
        // status — the same source PostSalesInvoiceAsync re-reads them from — so look them up there.
        var url = $"{_options.BaseUrl}/companies({companyId})/salesInvoices?$filter=externalDocumentNumber eq '{externalDocNumber}'";
        var invoices = await GetListAsync<BcSalesInvoice>(url);
        return invoices.FirstOrDefault(i => IsPostedStatus(i.Status));
    }

    /// <summary>A BC sales document status other than Draft/In Review means it has been posted.</summary>
    private static bool IsPostedStatus(string? status) =>
        !string.IsNullOrEmpty(status)
        && !status.Equals("Draft", StringComparison.OrdinalIgnoreCase)
        && !status.Equals("In Review", StringComparison.OrdinalIgnoreCase);

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

    public async Task<BcSalesInvoice?> PostSalesInvoiceAsync(Guid companyId, Guid invoiceId, string? externalDocNumber = null)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/salesInvoices({invoiceId})/Microsoft.NAV.post";
        _logger.LogInformation("POST {Url}", url);

        var response = await _httpClient.PostAsync(url, null);
        await EnsureSuccessAsync(response);

        // Strategy 1: re-fetch from salesInvoices by ID
        try
        {
            var fetchUrl = $"{_options.BaseUrl}/companies({companyId})/salesInvoices({invoiceId})";
            _logger.LogInformation("Re-fetch attempt 1 (by ID): {Url}", fetchUrl);
            var posted = await GetSingleAsync<BcSalesInvoice>(fetchUrl);
            if (posted != null) { _logger.LogInformation("Re-fetch by ID succeeded"); return posted; }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Re-fetch by ID failed: {Error}", ex.Message);
        }

        // Strategy 2: re-fetch from salesInvoices by external doc number
        if (!string.IsNullOrEmpty(externalDocNumber))
        {
            try
            {
                _logger.LogInformation("Re-fetch attempt 2 (by external doc): {ExtDoc}", externalDocNumber);
                var byDoc = await GetSalesInvoiceByExternalDocAsync(companyId, externalDocNumber);
                if (byDoc != null) { _logger.LogInformation("Re-fetch by external doc succeeded"); return byDoc; }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Re-fetch by external doc failed: {Error}", ex.Message);
            }
        }

        // Strategy 3: try the postedSalesInvoices endpoint (some BC versions move posted invoices there)
        if (!string.IsNullOrEmpty(externalDocNumber))
        {
            try
            {
                var postedUrl = $"{_options.BaseUrl}/companies({companyId})/salesInvoices?$filter=number eq '{externalDocNumber}' or externalDocumentNumber eq '{externalDocNumber}'&$orderby=lastModifiedDateTime desc&$top=1";
                _logger.LogInformation("Re-fetch attempt 3 (broad filter): {Url}", postedUrl);
                var items = await GetListAsync<BcSalesInvoice>(postedUrl);
                if (items.Count > 0) { _logger.LogInformation("Re-fetch by broad filter succeeded, found {Count} items", items.Count); return items[0]; }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Re-fetch by broad filter failed: {Error}", ex.Message);
            }
        }

        _logger.LogError("All re-fetch strategies failed for posted invoice {InvoiceId}, externalDoc={ExtDoc}", invoiceId, externalDocNumber);
        return null;
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

    public async Task DeleteSalesCreditMemoAsync(Guid companyId, Guid creditMemoId, string? etag = null)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/salesCreditMemos({creditMemoId})";
        _logger.LogInformation("DELETE {Url}", url);

        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Add("If-Match", etag ?? "*");
        var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);
    }

    public async Task<byte[]?> GetSalesInvoicePdfAsync(Guid companyId, Guid invoiceId)
    {
        await SetAuthHeaderAsync();
        // Get the pdfDocument entity — BC returns a single entity (not an array)
        var pdfDocUrl = $"{_options.BaseUrl}/companies({companyId})/salesInvoices({invoiceId})/pdfDocument";
        _logger.LogInformation("GET {Url} (pdfDocument)", pdfDocUrl);
        var pdfDocResponse = await _httpClient.GetAsync(pdfDocUrl);
        if (!pdfDocResponse.IsSuccessStatusCode)
        {
            var errBody = await pdfDocResponse.Content.ReadAsStringAsync();
            _logger.LogWarning("Could not get pdfDocument: {Status} {Body}", (int)pdfDocResponse.StatusCode, errBody);
            return null;
        }
        var pdfDocJson = await pdfDocResponse.Content.ReadAsStringAsync();

        // Extract the mediaReadLink for the PDF content
        string? contentUrl = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(pdfDocJson);
            var root = doc.RootElement;

            // BC may return single entity or array — handle both
            System.Text.Json.JsonElement entity;
            if (root.TryGetProperty("value", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                if (arr.GetArrayLength() == 0) { _logger.LogWarning("No pdfDocument found for invoice {Id}", invoiceId); return null; }
                entity = arr[0];
            }
            else
            {
                entity = root;
            }

            // Use the mediaReadLink if available
            if (entity.TryGetProperty("pdfDocumentContent@odata.mediaReadLink", out var linkProp))
                contentUrl = linkProp.GetString();
            else if (entity.TryGetProperty("id", out var idProp))
                contentUrl = $"{_options.BaseUrl}/companies({companyId})/salesInvoices({invoiceId})/pdfDocument/pdfDocumentContent";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse pdfDocument response: {Body}", pdfDocJson);
            return null;
        }

        if (string.IsNullOrEmpty(contentUrl))
        {
            _logger.LogWarning("No PDF content URL found for invoice {Id}", invoiceId);
            return null;
        }

        _logger.LogInformation("GET {Url} (PDF content)", contentUrl);
        var response = await _httpClient.GetAsync(contentUrl);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
        {
            _logger.LogInformation("Downloaded invoice PDF ({Bytes} bytes, status {Status})", bytes.Length, (int)response.StatusCode);
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

    public async Task<BcCustomerPayment> CreateCustomerPaymentAsync(Guid companyId, Guid journalId, BcCustomerPayment payment)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/customerPaymentJournals({journalId})/customerPayments";
        _logger.LogInformation("POST {Url}", url);

        var json = System.Text.Json.JsonSerializer.Serialize(payment);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content);
        await EnsureSuccessAsync(response);

        var result = await response.Content.ReadFromJsonAsync<BcCustomerPayment>();
        return result!;
    }

    public async Task PostCustomerPaymentJournalAsync(Guid companyId, Guid journalId)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/customerPaymentJournals({journalId})/Microsoft.NAV.post";
        _logger.LogInformation("POST {Url}", url);

        var response = await _httpClient.PostAsync(url, null);
        await EnsureSuccessAsync(response);
    }

    public async Task<List<BcCustomerLedgerEntry>> GetCustomerLedgerEntriesAsync(Guid companyId)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/customerLedgerEntries?$top=5000&$orderby=postingDate desc";
        return await GetListAsync<BcCustomerLedgerEntry>(url);
    }

    public async Task<List<BcCustomerLedgerEntry>> GetCustomerLedgerEntriesByCustomerAsync(Guid companyId, string customerNo, string documentType = "Payment", bool openOnly = true)
    {
        var filter = $"customerNo eq '{customerNo}' and documentType eq '{documentType}'";
        if (openOnly)
            filter += " and open eq true";
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/customerLedgerEntries?$filter={Uri.EscapeDataString(filter)}";
        return await GetListAsync<BcCustomerLedgerEntry>(url);
    }

    public async Task<BcPaymentApplication> ApplyPaymentToInvoiceAsync(Guid companyId, string customerNo, int paymentEntryNo, string invoiceDocumentNo, decimal amountToApply = 0)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/paymentApplications";
        var payload = new BcPaymentApplication
        {
            CustomerNo = customerNo,
            PaymentEntryNo = paymentEntryNo,
            InvoiceDocumentNo = invoiceDocumentNo,
            AmountToApply = amountToApply
        };
        return await PostAsync(url, payload);
    }

    public async Task<BcPaymentApplication> ApplyCreditMemoToInvoiceAsync(Guid companyId, string customerNo, int creditMemoEntryNo, string invoiceDocumentNo, decimal amountToApply = 0)
    {
        var url = $"{_options.CustomApiBaseUrl}/companies({companyId})/paymentApplications";
        var payload = new BcPaymentApplication
        {
            CustomerNo = customerNo,
            PaymentEntryNo = creditMemoEntryNo,
            InvoiceDocumentNo = invoiceDocumentNo,
            AmountToApply = amountToApply,
            SourceDocumentType = "CreditMemo"
        };
        return await PostAsync(url, payload);
    }

    public async Task<List<BcGeneralLedgerEntry>> GetGeneralLedgerEntriesAsync(Guid companyId, int top = 500)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/generalLedgerEntries?$top={top}&$orderby=postingDate desc";
        return await GetListAsync<BcGeneralLedgerEntry>(url);
    }

    public async Task<List<BcCustomerBalance>> GetCustomerBalancesAsync(Guid companyId)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/customers?$top=5000&$select=id,number,displayName,balance,overdueAmount,currencyCode";
        return await GetListAsync<BcCustomerBalance>(url);
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

    public async Task<BcSalesCreditMemo?> PostSalesCreditMemoAsync(Guid companyId, Guid creditMemoId, string? externalDocNumber = null)
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
            if (posted != null) return posted;
        }
        catch
        {
            _logger.LogWarning("Re-fetch by ID failed after posting credit memo {CreditMemoId}, trying by external doc number", creditMemoId);
        }

        if (!string.IsNullOrEmpty(externalDocNumber))
        {
            try
            {
                var byDoc = await GetSalesCreditMemoByExternalDocAsync(companyId, externalDocNumber);
                if (byDoc != null) return byDoc;
            }
            catch
            {
                _logger.LogWarning("Re-fetch by external doc number also failed for credit memo {ExtDoc}", externalDocNumber);
            }
        }

        return null;
    }

    public async Task<BcSalesCreditMemo?> GetSalesCreditMemoByExternalDocAsync(Guid companyId, string externalDocNumber)
    {
        var url = $"{_options.BaseUrl}/companies({companyId})/salesCreditMemos?$filter=externalDocumentNumber eq '{externalDocNumber}'";
        var items = await GetListAsync<BcSalesCreditMemo>(url);
        return items.FirstOrDefault();
    }

    /// <summary>Posted sales credit memo matching an external document number (used for idempotency on retry).</summary>
    public async Task<BcSalesCreditMemo?> GetPostedSalesCreditMemoByExternalDocAsync(Guid companyId, string externalDocNumber)
    {
        // As with invoices: no `postedSalesCreditMemos` resource in standard api/v2.0. Posted credit
        // memos remain in `salesCreditMemos` with a non-Draft status.
        var url = $"{_options.BaseUrl}/companies({companyId})/salesCreditMemos?$filter=externalDocumentNumber eq '{externalDocNumber}'";
        var items = await GetListAsync<BcSalesCreditMemo>(url);
        return items.FirstOrDefault(c => IsPostedStatus(c.Status));
    }

    public async Task<byte[]?> GetSalesCreditMemoPdfAsync(Guid companyId, Guid creditMemoId)
    {
        await SetAuthHeaderAsync();
        var pdfDocUrl = $"{_options.BaseUrl}/companies({companyId})/salesCreditMemos({creditMemoId})/pdfDocument";
        _logger.LogInformation("GET {Url} (pdfDocument for credit memo)", pdfDocUrl);
        var pdfDocResponse = await _httpClient.GetAsync(pdfDocUrl);
        if (!pdfDocResponse.IsSuccessStatusCode)
        {
            var errBody = await pdfDocResponse.Content.ReadAsStringAsync();
            _logger.LogWarning("Could not get pdfDocument for credit memo: {Status} {Body}", (int)pdfDocResponse.StatusCode, errBody);
            return null;
        }
        var pdfDocJson = await pdfDocResponse.Content.ReadAsStringAsync();

        string? contentUrl = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(pdfDocJson);
            var root = doc.RootElement;

            System.Text.Json.JsonElement entity;
            if (root.TryGetProperty("value", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                if (arr.GetArrayLength() == 0) { _logger.LogWarning("No pdfDocument found for credit memo {Id}", creditMemoId); return null; }
                entity = arr[0];
            }
            else
            {
                entity = root;
            }

            if (entity.TryGetProperty("pdfDocumentContent@odata.mediaReadLink", out var linkProp))
                contentUrl = linkProp.GetString();
            else if (entity.TryGetProperty("id", out var idProp))
                contentUrl = $"{_options.BaseUrl}/companies({companyId})/salesCreditMemos({creditMemoId})/pdfDocument/pdfDocumentContent";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse pdfDocument response for credit memo: {Body}", pdfDocJson);
            return null;
        }

        if (string.IsNullOrEmpty(contentUrl))
        {
            _logger.LogWarning("No PDF content URL found for credit memo {Id}", creditMemoId);
            return null;
        }

        _logger.LogInformation("GET {Url} (credit memo PDF content)", contentUrl);
        var response = await _httpClient.GetAsync(contentUrl);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
        {
            _logger.LogInformation("Downloaded credit memo PDF ({Bytes} bytes, status {Status})", bytes.Length, (int)response.StatusCode);
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

    // ── Custom PDF Generation (via BC extension) ───────────────

    public async Task<(bool success, string? error, int reportId)> RequestPdfGenerationAsync(
        Guid companyId, string documentNo, string documentType = "Sales Invoice")
    {
        await SetAuthHeaderAsync();
        var pdfApiBase = $"https://api.businesscentral.dynamics.com/v2.0/{_options.TenantId}/{_options.Environment}/api/auctionSystem/pdf/v1.0";
        var url = $"{pdfApiBase}/companies({companyId})/invoicePdfRequests";
        _logger.LogInformation("POST {Url} (custom PDF generation for {DocNo})", url, documentNo);

        var docTypeStr = documentType == "Credit Memo" ? "Credit Memo" : "Sales Invoice";
        var payload = new { documentNo, documentType = docTypeStr };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("Custom PDF response: {Status} {Body}", (int)response.StatusCode, body);

        if (!response.IsSuccessStatusCode)
            return (false, $"HTTP {(int)response.StatusCode}: {body}", 0);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var successProp) && successProp.GetBoolean();
            var errorMsg = root.TryGetProperty("errorMessage", out var errProp) ? errProp.GetString() : null;
            var reportId = root.TryGetProperty("reportIdUsed", out var repProp) ? repProp.GetInt32() : 0;
            return (success, errorMsg, reportId);
        }
        catch
        {
            return (false, $"Could not parse response: {body}", 0);
        }
    }

    public async Task<byte[]?> GetDocumentAttachmentPdfAsync(Guid companyId, Guid invoiceId, string entityType = "salesInvoices")
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/{entityType}({invoiceId})/documentAttachments";
        _logger.LogInformation("GET {Url} (document attachments)", url);

        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Could not get document attachments: {Status}", (int)response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var values = doc.RootElement.GetProperty("value");

        // Find the PDF attachment
        foreach (var att in values.EnumerateArray())
        {
            var fileName = att.TryGetProperty("fileName", out var fn) ? fn.GetString() : "";
            if (fileName != null && fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var attId = att.GetProperty("id").GetString();
                // Download attachment content
                var contentUrl = $"{_options.BaseUrl}/companies({companyId})/{entityType}({invoiceId})/documentAttachments({attId})/attachmentContent";
                _logger.LogInformation("GET {Url} (attachment content)", contentUrl);

                var contentResponse = await _httpClient.GetAsync(contentUrl);
                if (contentResponse.IsSuccessStatusCode)
                {
                    var bytes = await contentResponse.Content.ReadAsByteArrayAsync();
                    if (bytes.Length > 0)
                    {
                        _logger.LogInformation("Downloaded attachment PDF: {Bytes} bytes", bytes.Length);
                        return bytes;
                    }
                }
            }
        }

        _logger.LogWarning("No PDF attachment found for {EntityType}({InvoiceId})", entityType, invoiceId);
        return null;
    }

    // ── Diagnostics ─────────────────────────────────────────────

    public async Task<string> GetCompanyInformationRawAsync(Guid companyId)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/companyInformation";
        _logger.LogInformation("GET {Url} (diagnostic)", url);
        var response = await _httpClient.GetAsync(url);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<string> GetBankAccountsRawAsync(Guid companyId)
    {
        await SetAuthHeaderAsync();
        var url = $"{_options.BaseUrl}/companies({companyId})/bankAccounts";
        _logger.LogInformation("GET {Url} (diagnostic)", url);
        var response = await _httpClient.GetAsync(url);
        return await response.Content.ReadAsStringAsync();
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
        var results = new List<T>();
        var next = url;
        // Follow @odata.nextLink so a result set larger than BC's page size is fully read instead of
        // silently truncated at the first page. The page guard is a safety net against a bad nextLink.
        for (var page = 0; !string.IsNullOrEmpty(next) && page < 1000; page++)
        {
            await SetAuthHeaderAsync();
            _logger.LogDebug("GET {Url}", next);

            var response = await _httpClient.GetAsync(next);
            await EnsureSuccessAsync(response);

            var odata = await response.Content.ReadFromJsonAsync<ODataResponse<T>>(JsonOptions);
            if (odata?.Value != null) results.AddRange(odata.Value);
            next = odata?.NextLink;
        }
        return results;
    }

    private async Task<T?> GetSingleAsync<T>(string url) where T : class
    {
        await SetAuthHeaderAsync();
        _logger.LogDebug("GET {Url}", url);

        var response = await _httpClient.GetAsync(url);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    private async Task<T> PostAsync<T>(string url, T payload)
    {
        await SetAuthHeaderAsync();
        _logger.LogDebug("POST {Url}", url);

        var response = await _httpClient.PostAsJsonAsync(url, payload, JsonOptions);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private async Task<T> PatchAsync<T>(string url, T payload, string? etag)
    {
        await SetAuthHeaderAsync();
        _logger.LogDebug("PATCH {Url}", url);

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
