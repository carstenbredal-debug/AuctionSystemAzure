using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using KSeF.Functions.Models;
using KSeF.Functions.Services;

namespace KSeF.Functions.Functions;

/// <summary>
/// Azure Function endpoints for KSeF invoice operations.
/// Called by Business Central to submit invoices and check status.
/// </summary>
public class InvoiceFunctions
{
    private readonly KSeFApiClient _ksef;
    private readonly InvoiceXmlBuilder _xmlBuilder;
    private readonly FaValidator _validator;
    private readonly ILogger<InvoiceFunctions> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public InvoiceFunctions(KSeFApiClient ksef, InvoiceXmlBuilder xmlBuilder, FaValidator validator, ILogger<InvoiceFunctions> logger)
    {
        _ksef = ksef;
        _xmlBuilder = xmlBuilder;
        _validator = validator;
        _logger = logger;
    }

    /// <summary>
    /// Submit an invoice to KSeF.
    /// POST /api/invoice/submit
    /// Body: InvoiceData JSON
    /// Returns: SubmitResult with element reference number
    /// </summary>
    [Function("SubmitInvoice")]
    public async Task<HttpResponseData> SubmitInvoice(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "invoice/submit")] HttpRequestData req)
    {
        _logger.LogInformation("SubmitInvoice called");

        try
        {
            var body = await req.ReadAsStringAsync();
            var invoice = JsonSerializer.Deserialize<InvoiceData>(body!, JsonOpts);

            if (invoice == null)
                // Malformed payload — a retry of the same body will never help. Terminal.
                return await CreateResponse(req, HttpStatusCode.BadRequest,
                    new SubmitResult { Success = false, Retryable = false, Error = "Invalid invoice data" });

            // 1. Build FA(3) XML
            var xml = _xmlBuilder.Build(invoice);
            _logger.LogInformation("Generated FA(3) XML for invoice {Number}", invoice.InvoiceNumber);

            // 1b. Validate against the official FA(3) XSD BEFORE sending — never submit an invalid
            // document to KSeF. A schema failure is TERMINAL: the same document will never validate,
            // so BC must mark it Rejected and stop retrying (Retryable = false).
            var validation = _validator.Validate(xml);
            if (!validation.IsValid)
            {
                _logger.LogError("FA(3) schema validation failed for invoice {Number}: {Errors}",
                    invoice.InvoiceNumber, string.Join(" | ", validation.Errors));
                return await CreateResponse(req, HttpStatusCode.BadRequest, new SubmitResult
                {
                    Success = false,
                    Retryable = false,
                    Error = "FA(3) schema validation failed: " + string.Join(" | ", validation.Errors)
                });
            }

            var xmlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();

            // 2. Authenticate / open a KSeF session. A failure here is before the invoice reached
            //    KSeF — classify it (a 4xx is terminal, 5xx/throttle/timeout is retryable).
            KSeFSessionResponse session;
            try
            {
                session = await _ksef.InitSessionAsync(invoice.Seller.NIP);
            }
            catch (KSeFException kex)
            {
                _logger.LogError(kex, "KSeF auth/session failed for invoice {Number}", invoice.InvoiceNumber);
                return await CreateResponse(req, HttpStatusCode.BadGateway,
                    new SubmitResult { Success = false, Retryable = kex.Retryable, Error = "KSeF auth failed: " + kex.Message });
            }
            _logger.LogInformation("KSeF session started: {Token}", session.SessionToken[..8] + "...");

            // 3. Send the invoice. A failure HERE means it never reached KSeF, so classify:
            //    duplicate (already in KSeF) / terminal reject / transient.
            KSeFSendResponse sendResult;
            try
            {
                sendResult = await _ksef.SendInvoiceAsync(xml, session.SessionToken);
            }
            catch (KSeFException kex)
            {
                await SafeTerminateAsync(session.SessionToken);
                if (kex.Duplicate)
                {
                    _logger.LogWarning("KSeF reports invoice {Number} already submitted (duplicate)", invoice.InvoiceNumber);
                    return await CreateResponse(req, HttpStatusCode.OK, new SubmitResult
                    {
                        Success = false, Duplicate = true, Retryable = false,
                        Error = "Already submitted to KSeF (duplicate): " + kex.Message
                    });
                }
                _logger.LogError(kex, "KSeF send failed for invoice {Number} (retryable={Retry})", invoice.InvoiceNumber, kex.Retryable);
                return await CreateResponse(req, kex.Retryable ? HttpStatusCode.BadGateway : HttpStatusCode.BadRequest,
                    new SubmitResult { Success = false, Retryable = kex.Retryable, Error = "KSeF send failed: " + kex.Message });
            }
            _logger.LogInformation("Invoice sent, ref: {Ref}", sendResult.ElementReferenceNumber);

            // ===== POST-SEND GUARANTEE =====
            // The invoice is now IN KSeF (we hold its element reference). NOTHING below may turn
            // this into a failure: a poll or terminate hiccup must never cause BC to re-send the
            // invoice (that would create the duplicate we're trying to prevent). We only enrich
            // the result with the final KSeF number when it's ready.
            string? ksefNumber = null;
            var duplicate = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(3000);
                try
                {
                    var status = await _ksef.GetInvoiceStatusBySessionAsync(
                        session.SessionReferenceNumber, sendResult.ElementReferenceNumber, session.SessionToken);

                    if (status.ProcessingCode == 200 && !string.IsNullOrEmpty(status.KSeFReferenceNumber))
                    {
                        ksefNumber = status.KSeFReferenceNumber;
                        _logger.LogInformation("KSeF accepted on attempt {Attempt}: {Number}", attempt + 1, ksefNumber);
                        break;
                    }
                    if (IsDuplicate(status.ProcessingCode, status.ProcessingDescription))
                    {
                        duplicate = true;
                        ksefNumber ??= status.KSeFReferenceNumber;
                        _logger.LogWarning("KSeF status reports duplicate for invoice {Number}", invoice.InvoiceNumber);
                        break;
                    }
                    if (status.ProcessingCode >= 400)
                    {
                        // Terminal KSeF rejection AFTER send — stop polling; BC marks it Rejected (no retry).
                        _logger.LogError("KSeF rejected invoice {Number}: code={Code} {Desc}",
                            invoice.InvoiceNumber, status.ProcessingCode, status.ProcessingDescription);
                        await SafeTerminateAsync(session.SessionToken);
                        return await CreateResponse(req, HttpStatusCode.OK, new SubmitResult
                        {
                            Success = false, Retryable = false,
                            ElementReferenceNumber = sendResult.ElementReferenceNumber,
                            Error = $"KSeF rejected (code {status.ProcessingCode}): {status.ProcessingDescription}"
                        });
                    }
                    _logger.LogInformation("KSeF status poll attempt {Attempt}: code={Code}", attempt + 1, status.ProcessingCode);
                }
                catch (Exception pollEx)
                {
                    // Non-fatal: the invoice IS in KSeF; we just couldn't read its status this attempt.
                    _logger.LogWarning(pollEx, "Status poll attempt {Attempt} failed (non-fatal)", attempt + 1);
                }
            }

            await SafeTerminateAsync(session.SessionToken);

            var refForQr = !string.IsNullOrEmpty(ksefNumber) ? ksefNumber : sendResult.ElementReferenceNumber;
            var qrUrl = $"{_ksef.BaseUrl.Replace("/v2", "")}/web/verify/{refForQr}/{xmlHash}";

            // Accepted (has number) or Sent/pending (no number yet — BC reconciles via status, never
            // re-sends). Duplicate => already in KSeF; BC marks Accepted.
            return await CreateResponse(req, HttpStatusCode.OK, new SubmitResult
            {
                Success = !duplicate,
                Duplicate = duplicate,
                Retryable = false,
                ElementReferenceNumber = sendResult.ElementReferenceNumber,
                KSeFReferenceNumber = ksefNumber,
                SessionToken = session.SessionToken,
                SessionReferenceNumber = session.SessionReferenceNumber,
                QRVerificationUrl = qrUrl
            });
        }
        catch (Exception ex)
        {
            // Unexpected failure BEFORE the invoice was confirmed in KSeF → transient by default,
            // safe for BC to retry (capped on the BC side).
            _logger.LogError(ex, "SubmitInvoice failed");
            return await CreateResponse(req, HttpStatusCode.InternalServerError,
                new SubmitResult { Success = false, Retryable = true, Error = ex.Message });
        }
    }

    /// <summary>
    /// Check the status of a previously submitted invoice.
    /// GET /api/invoice/status/{elementReferenceNumber}?nip={sellerNip}&amp;sessionRef={sessionReferenceNumber}
    /// Returns: StatusResult with processing code and KSeF reference number
    /// </summary>
    [Function("GetInvoiceStatus")]
    public async Task<HttpResponseData> GetInvoiceStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "invoice/status/{elementReferenceNumber}")] HttpRequestData req,
        string elementReferenceNumber)
    {
        _logger.LogInformation("GetInvoiceStatus called for {Ref}", elementReferenceNumber);

        try
        {
            var nip = req.Query["nip"];
            if (string.IsNullOrEmpty(nip))
                return await CreateResponse(req, HttpStatusCode.BadRequest,
                    new StatusResult { Success = false, Error = "Missing 'nip' query parameter" });

            var sessionRef = req.Query["sessionRef"];
            if (string.IsNullOrEmpty(sessionRef))
                return await CreateResponse(req, HttpStatusCode.BadRequest,
                    new StatusResult { Success = false, Error = "Missing 'sessionRef' query parameter" });

            // Authenticate to get access token (no need to open a new session)
            var session = await _ksef.InitSessionAsync(nip);

            // Query invoice status from the original session
            var status = await _ksef.GetInvoiceStatusBySessionAsync(sessionRef, elementReferenceNumber, session.SessionToken);

            await _ksef.TerminateSessionAsync(session.SessionToken);

            var qrUrl = !string.IsNullOrEmpty(status.KSeFReferenceNumber)
                ? $"{_ksef.BaseUrl.Replace("/v2", "")}/web/verify/{status.KSeFReferenceNumber}"
                : null;

            return await CreateResponse(req, HttpStatusCode.OK, new StatusResult
            {
                Success = true,
                ProcessingCode = status.ProcessingCode,
                ProcessingDescription = status.ProcessingDescription,
                Details = status.Details,
                KSeFReferenceNumber = status.KSeFReferenceNumber,
                AcquisitionTimestamp = status.AcquisitionTimestamp,
                QRVerificationUrl = qrUrl
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetInvoiceStatus failed");
            return await CreateResponse(req, HttpStatusCode.InternalServerError,
                new StatusResult { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Generate invoice XML preview without sending to KSeF.
    /// POST /api/invoice/preview
    /// Body: InvoiceData JSON
    /// Returns: FA(3) XML string. Response headers carry the schema-validation result
    /// (X-FA3-Valid; X-FA3-Validation-Error-Count when invalid — full errors are logged).
    /// </summary>
    [Function("PreviewInvoiceXml")]
    public async Task<HttpResponseData> PreviewInvoiceXml(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "invoice/preview")] HttpRequestData req)
    {
        _logger.LogInformation("PreviewInvoiceXml called");

        try
        {
            var body = await req.ReadAsStringAsync();
            var invoice = JsonSerializer.Deserialize<InvoiceData>(body!, JsonOpts);

            if (invoice == null)
            {
                var badResp = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResp.WriteStringAsync("Invalid invoice data");
                return badResp;
            }

            var xml = _xmlBuilder.Build(invoice);

            var validation = _validator.Validate(xml);
            if (!validation.IsValid)
                _logger.LogWarning("FA(3) preview for invoice {Number} is not schema-valid: {Errors}",
                    invoice.InvoiceNumber, string.Join(" | ", validation.Errors));

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/xml");
            response.Headers.Add("X-FA3-Valid", validation.IsValid ? "true" : "false");
            if (!validation.IsValid)
                response.Headers.Add("X-FA3-Validation-Error-Count", validation.Errors.Count.ToString());
            await response.WriteStringAsync(xml);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PreviewInvoiceXml failed");
            var errResp = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errResp.WriteStringAsync(ex.Message);
            return errResp;
        }
    }

    /// <summary>
    /// Health check endpoint.
    /// GET /api/health
    /// </summary>
    [Function("HealthCheck")]
    public async Task<HttpResponseData> HealthCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            status = "healthy",
            service = "KSeFIntegration",
            timestamp = DateTime.UtcNow
        }));
        return response;
    }

    private static async Task<HttpResponseData> CreateResponse<T>(HttpRequestData req, HttpStatusCode status, T body)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOpts));
        return response;
    }

    /// <summary>
    /// Close a KSeF session without ever throwing — part of the post-send guarantee. Once the
    /// invoice is in KSeF, a failed terminate must not propagate and turn a success into a retry.
    /// </summary>
    private async Task SafeTerminateAsync(string? sessionToken)
    {
        try
        {
            await _ksef.TerminateSessionAsync(sessionToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KSeF session terminate failed (non-fatal — invoice already submitted)");
        }
    }

    /// <summary>
    /// KSeF "Duplikat faktury" — processing code 440, or the description mentions it. A duplicate
    /// means the invoice is already accepted in KSeF, so it is terminal (never retry).
    /// </summary>
    private static bool IsDuplicate(int processingCode, string? description) =>
        processingCode == 440
        || (description?.Contains("Duplikat", StringComparison.OrdinalIgnoreCase) ?? false);
}
