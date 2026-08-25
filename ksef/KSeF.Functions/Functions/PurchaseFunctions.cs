using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using KSeF.Functions.Services;

namespace KSeF.Functions.Functions;

/// <summary>
/// Incoming (purchase) invoices from KSeF. GET /api/purchases/view serves a self-contained
/// browser page; GET /api/purchases proxies the KSeF metadata query (Subject2 = the company as
/// buyer) and returns KSeF's raw invoice metadata untouched, so the page survives schema drift.
/// Same access model as the other pages: function-key in the URL.
/// </summary>
public class PurchaseFunctions
{
    private readonly KSeFApiClient _ksef;
    private readonly ILogger<PurchaseFunctions> _logger;

    private static readonly Lazy<string> Page = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("KSeF.Functions.Resources.IncomingInvoices.html")
            ?? throw new InvalidOperationException("IncomingInvoices.html not found in embedded resources.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    public PurchaseFunctions(KSeFApiClient ksef, ILogger<PurchaseFunctions> logger)
    {
        _ksef = ksef;
        _logger = logger;
    }

    [Function("PurchasesPage")]
    public async Task<HttpResponseData> View(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "purchases/view")] HttpRequestData req)
    {
        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "text/html; charset=utf-8");
        await resp.WriteStringAsync(Page.Value);
        return resp;
    }

    [Function("PurchasesQuery")]
    public async Task<HttpResponseData> Query(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "purchases")] HttpRequestData req)
    {
        var nip = req.Query["nip"];
        if (string.IsNullOrEmpty(nip))
            return await Json(req, HttpStatusCode.BadRequest, "{\"success\":false,\"error\":\"Missing 'nip' query parameter\"}");

        if (!DateTime.TryParse(req.Query["from"], out var from) ||
            !DateTime.TryParse(req.Query["to"], out var to))
            return await Json(req, HttpStatusCode.BadRequest, "{\"success\":false,\"error\":\"Missing or invalid 'from'/'to' date (yyyy-MM-dd)\"}");

        // Subject2 = the company as BUYER (incoming); Subject1 allowed for a sales view.
        var subjectType = req.Query["subjectType"];
        if (subjectType != "Subject1") subjectType = "Subject2";
        var dateType = req.Query["dateType"];
        if (dateType != "Invoicing") dateType = "Issue";
        int.TryParse(req.Query["pageOffset"], out var pageOffset);

        try
        {
            var (invoicesJson, hasMore) = await _ksef.QueryInvoicesMetadataRawAsync(
                nip!, subjectType!, dateType!, from, to, pageOffset);
            return await Json(req, HttpStatusCode.OK,
                $"{{\"success\":true,\"hasMore\":{(hasMore ? "true" : "false")},\"invoices\":{invoicesJson}}}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Purchases query failed for NIP {NIP}", nip);
            return await Json(req, HttpStatusCode.InternalServerError,
                System.Text.Json.JsonSerializer.Serialize(new { success = false, error = ex.Message }));
        }
    }

    private static async Task<HttpResponseData> Json(HttpRequestData req, HttpStatusCode code, string body)
    {
        var resp = req.CreateResponse(code);
        resp.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await resp.WriteStringAsync(body);
        return resp;
    }
}
