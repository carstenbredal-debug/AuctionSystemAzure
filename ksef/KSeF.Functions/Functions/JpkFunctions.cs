using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using KSeF.Functions.Models;
using KSeF.Functions.Services;

namespace KSeF.Functions.Functions;

public class JpkFunctions
{
    private readonly JpkV7MBuilder _jpkBuilder;
    private readonly JpkValidator _jpkValidator;
    private readonly ILogger<JpkFunctions> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JpkFunctions(JpkV7MBuilder jpkBuilder, JpkValidator jpkValidator, ILogger<JpkFunctions> logger)
    {
        _jpkBuilder = jpkBuilder;
        _jpkValidator = jpkValidator;
        _logger = logger;
    }

    [Function("JpkV7MGenerate")]
    public async Task<HttpResponseData> Generate(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "jpk/v7m/generate")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
            {
                return await WriteJson(req, HttpStatusCode.BadRequest, new JpkV7MResponse
                {
                    Success = false,
                    Error = "Request body is empty."
                });
            }

            var request = JsonSerializer.Deserialize<JpkV7MRequest>(body, JsonOpts);
            if (request == null)
            {
                return await WriteJson(req, HttpStatusCode.BadRequest, new JpkV7MResponse
                {
                    Success = false,
                    Error = "Invalid request format."
                });
            }

            _logger.LogInformation("Generating JPK_V7M for {Year}/{Month} with {Count} sales records",
                request.Year, request.Month, request.SalesRecords.Count);

            var (xml, salesCount, taxDue) = _jpkBuilder.Build(request);

            var fileName = $"JPK_V7M_{request.Year}_{request.Month:D2}.xml";

            _logger.LogInformation("JPK_V7M generated: {SalesCount} sales records, tax due: {TaxDue}",
                salesCount, taxDue);

            var validation = _jpkValidator.Validate(xml);
            _logger.LogInformation("JPK_V7M validation: {IsValid}, errors: {ErrorCount}",
                validation.IsValid, validation.Errors.Count);

            return await WriteJson(req, HttpStatusCode.OK, new JpkV7MResponse
            {
                Success = true,
                Xml = xml,
                FileName = fileName,
                SalesRecordCount = salesCount,
                PurchaseRecordCount = 0,
                TaxDue = taxDue,
                TaxDeductible = 0,
                SchemaValid = validation.IsValid,
                ValidationErrors = validation.Errors
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JPK_V7M generation failed");
            return await WriteJson(req, HttpStatusCode.InternalServerError, new JpkV7MResponse
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    private static async Task<HttpResponseData> WriteJson<T>(HttpRequestData req, HttpStatusCode status, T body)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        var json = JsonSerializer.Serialize(body, JsonOpts);
        await response.WriteStringAsync(json);
        return response;
    }
}
