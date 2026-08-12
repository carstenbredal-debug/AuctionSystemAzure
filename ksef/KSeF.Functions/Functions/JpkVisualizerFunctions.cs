using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using KSeF.Functions.Services;

namespace KSeF.Functions.Functions;

/// <summary>
/// Browser-facing JPK_V7M visualiser. GET serves a self-contained HTML page that parses a
/// JPK XML entirely client-side (registers, declaration, consistency checks, two-file diff);
/// POST validates an uploaded XML against the embedded ministry XSDs. Same access model as
/// the KSeF invoice viewer: function-key in the URL.
/// </summary>
public class JpkVisualizerFunctions
{
    private readonly JpkValidator _validator;
    private readonly ILogger<JpkVisualizerFunctions> _logger;

    private static readonly Lazy<string> Page = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("KSeF.Functions.Resources.JpkVisualizer.html")
            ?? throw new InvalidOperationException("JpkVisualizer.html not found in embedded resources.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    public JpkVisualizerFunctions(JpkValidator validator, ILogger<JpkVisualizerFunctions> logger)
    {
        _validator = validator;
        _logger = logger;
    }

    [Function("JpkVisualizer")]
    public async Task<HttpResponseData> Visualizer(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "jpk/visualizer")] HttpRequestData req)
    {
        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "text/html; charset=utf-8");
        await resp.WriteStringAsync(Page.Value);
        return resp;
    }

    [Function("JpkV7MValidate")]
    public async Task<HttpResponseData> Validate(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "jpk/v7m/validate")] HttpRequestData req)
    {
        var xml = await req.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(xml))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await bad.WriteStringAsync("{\"success\":false,\"error\":\"Request body is empty — POST the JPK XML.\"}");
            return bad;
        }

        try
        {
            var result = _validator.Validate(xml);
            var resp = req.CreateResponse(HttpStatusCode.OK);
            resp.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new
            {
                success = true,
                schemaValid = result.IsValid,
                errors = result.Errors
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return resp;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JPK XSD validation failed");
            var resp = req.CreateResponse(HttpStatusCode.InternalServerError);
            resp.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new { success = false, error = ex.Message }));
            return resp;
        }
    }
}
