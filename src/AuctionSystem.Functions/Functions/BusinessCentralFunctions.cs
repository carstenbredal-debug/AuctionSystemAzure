using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AuctionSystem.Functions.BusinessCentral.Configuration;
using AuctionSystem.Functions.BusinessCentral.Services;

namespace AuctionSystem.Functions.Functions;

public class BusinessCentralFunctions
{
    private readonly BusinessCentralSyncService? _syncService;
    private readonly BusinessCentralApiClient? _bcClient;
    private readonly BusinessCentralOptions _options;
    private readonly ILogger<BusinessCentralFunctions> _logger;

    public BusinessCentralFunctions(
        IOptions<BusinessCentralOptions> options,
        ILogger<BusinessCentralFunctions> logger,
        BusinessCentralSyncService? syncService = null,
        BusinessCentralApiClient? bcClient = null)
    {
        _options = options.Value;
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
