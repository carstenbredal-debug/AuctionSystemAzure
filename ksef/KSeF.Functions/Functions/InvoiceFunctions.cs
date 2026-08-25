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
    /// Fetch an invoice / credit note from KSeF by its KSeF number and render it as a
    /// readable HTML page (with the raw XML collapsible at the bottom).
    /// GET /api/invoice/view/{ksefNumber}?nip={ourNip}
    /// </summary>
    [Function("ViewInvoice")]
    public async Task<HttpResponseData> ViewInvoice(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "invoice/view/{ksefNumber}")] HttpRequestData req,
        string ksefNumber)
    {
        var nip = req.Query["nip"];
        if (string.IsNullOrEmpty(nip))
            return await CreateResponse(req, HttpStatusCode.BadRequest,
                new StatusResult { Success = false, Error = "Missing 'nip' query parameter" });

        try
        {
            // Cached access token first (cheap — browsing a list of invoices must not do one full
            // auth handshake per view); fall back to the original per-view session on failure.
            string xml;
            try
            {
                await _ksef.EnsureQueryTokenAsync(nip!);
                xml = await _ksef.GetInvoiceXmlByKsefNumberAsync(ksefNumber);
            }
            catch (Exception tokenEx)
            {
                _logger.LogWarning(tokenEx, "Token-based invoice fetch failed for {KsefNumber}; falling back to session", ksefNumber);
                _ksef.InvalidateQueryToken(nip!);
                var session = await _ksef.InitSessionAsync(nip);
                try
                {
                    xml = await _ksef.GetInvoiceXmlByKsefNumberAsync(ksefNumber, session.SessionToken);
                }
                finally
                {
                    await _ksef.TerminateSessionAsync(session.SessionToken);
                }
            }

            var html = RenderInvoiceHtml(ksefNumber, xml);
            var resp = req.CreateResponse(HttpStatusCode.OK);
            resp.Headers.Add("Content-Type", "text/html; charset=utf-8");
            await resp.WriteStringAsync(html);
            return resp;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ViewInvoice failed for {KsefNumber}", ksefNumber);
            var resp = req.CreateResponse(HttpStatusCode.InternalServerError);
            resp.Headers.Add("Content-Type", "text/html; charset=utf-8");
            await resp.WriteStringAsync($"<html><body><h2>Could not fetch invoice</h2><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p></body></html>");
            return resp;
        }
    }

    // Namespace-agnostic FA(2)/FA(3) rendering: query by local element name so both schema
    // generations display. Unknown/missing elements render blank, never throw.
    private static string RenderInvoiceHtml(string ksefNumber, string xml)
    {
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        string E(string name) => System.Net.WebUtility.HtmlEncode(name);
        string Val(System.Xml.Linq.XElement? scope, params string[] path)
        {
            var cur = scope;
            foreach (var p in path)
            {
                cur = cur?.Elements().FirstOrDefault(e => e.Name.LocalName == p);
                if (cur == null) return "";
            }
            return System.Net.WebUtility.HtmlEncode(cur?.Value ?? "");
        }

        var root = doc.Root!;
        var fa = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Fa");
        var podmiot1 = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Podmiot1");
        var podmiot2 = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Podmiot2");

        string PartyBlock(System.Xml.Linq.XElement? p)
        {
            if (p == null) return "";
            var ident = p.Elements().FirstOrDefault(e => e.Name.LocalName == "DaneIdentyfikacyjne");
            var adres = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Adres");
            var nipEl = ident?.Elements().FirstOrDefault(e => e.Name.LocalName is "NIP" or "NrVatUE");
            var name = ident?.Elements().FirstOrDefault(e => e.Name.LocalName == "Nazwa")?.Value ?? "";
            var lines = adres?.Elements().Where(e => e.Name.LocalName.StartsWith("AdresL")).Select(e => e.Value) ?? Enumerable.Empty<string>();
            return $"<strong>{E(name)}</strong><br/>{(nipEl != null ? $"NIP/VAT: {E(nipEl.Value)}<br/>" : "")}{string.Join("<br/>", lines.Select(E))}";
        }

        var rodzaj = fa?.Elements().FirstOrDefault(e => e.Name.LocalName == "RodzajFaktury")?.Value ?? "";
        var docKind = rodzaj switch { "KOR" => "Credit note / correction (KOR)", "VAT" => "Invoice (VAT)", _ => rodzaj };
        var currency = fa?.Elements().FirstOrDefault(e => e.Name.LocalName == "KodWaluty")?.Value ?? "";

        var sb = new StringBuilder();
        sb.Append("<html><head><meta charset='utf-8'/><title>").Append(E(ksefNumber)).Append("</title><style>");
        sb.Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222}h2{margin-bottom:2px}");
        sb.Append(".meta{color:#666;margin-bottom:16px}.grid{display:flex;gap:40px;margin-bottom:16px}");
        sb.Append("table{border-collapse:collapse;width:100%;margin-bottom:16px}th,td{border:1px solid #ccc;padding:6px 10px;font-size:14px;text-align:left}");
        sb.Append("th{background:#f2f2f2}td.num,th.num{text-align:right}details{margin-top:24px}pre{background:#f7f7f7;padding:12px;overflow:auto;font-size:12px}");
        sb.Append("</style></head><body>");

        sb.Append("<h2>").Append(E(docKind)).Append(" — ").Append(Val(fa, "P_2")).Append("</h2>");
        sb.Append("<div class='meta'>KSeF number: <strong>").Append(E(ksefNumber)).Append("</strong>");
        sb.Append(" · Issue date: ").Append(Val(fa, "P_1"));
        if (!string.IsNullOrEmpty(currency)) sb.Append(" · Currency: ").Append(E(currency));
        sb.Append("</div>");

        sb.Append("<div class='grid'><div><h4>Seller (Podmiot1)</h4>").Append(PartyBlock(podmiot1)).Append("</div>");
        sb.Append("<div><h4>Buyer (Podmiot2)</h4>").Append(PartyBlock(podmiot2)).Append("</div></div>");

        // Correction reference (KOR only)
        var korekta = fa?.Descendants().FirstOrDefault(e => e.Name.LocalName == "DaneFaKorygowanej");
        if (korekta != null)
        {
            sb.Append("<p><strong>Corrects:</strong> ").Append(Val(korekta, "NrFaKorygowanej"))
              .Append(" of ").Append(Val(korekta, "DataWystFaKorygowanej"));
            var origKsef = korekta.Elements().FirstOrDefault(e => e.Name.LocalName == "NrKSeFFaKorygowanej")?.Value;
            if (!string.IsNullOrEmpty(origKsef)) sb.Append(" (KSeF ").Append(E(origKsef)).Append(")");
            sb.Append("</p>");
        }

        sb.Append("<table><tr><th>#</th><th>Description</th><th class='num'>Qty</th><th>Unit</th><th class='num'>Unit price</th><th class='num'>Net</th><th>VAT</th></tr>");
        foreach (var w in fa?.Elements().Where(e => e.Name.LocalName == "FaWiersz") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            string WV(string n) => System.Net.WebUtility.HtmlEncode(w.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value ?? "");
            sb.Append("<tr><td>").Append(WV("NrWierszaFa")).Append("</td><td>").Append(WV("P_7"))
              .Append("</td><td class='num'>").Append(WV("P_8B")).Append("</td><td>").Append(WV("P_8A"))
              .Append("</td><td class='num'>").Append(WV("P_9A")).Append("</td><td class='num'>").Append(WV("P_11"))
              .Append("</td><td>").Append(WV("P_12")).Append("</td></tr>");
        }
        sb.Append("</table>");

        sb.Append("<h3>Total due: ").Append(Val(fa, "P_15"));
        if (!string.IsNullOrEmpty(currency)) sb.Append(" ").Append(E(currency));
        sb.Append("</h3>");

        sb.Append("<details><summary>Raw XML</summary><pre>")
          .Append(System.Net.WebUtility.HtmlEncode(doc.ToString()))
          .Append("</pre></details>");
        sb.Append("</body></html>");
        return sb.ToString();
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
            // re-sends). Duplicate => the invoice IS in KSeF: with its number we can truthfully
            // report success (BC stores the number and marks Accepted); without it, return a clear
            // error — BC's fallback for a missing error field is a useless "Unknown error".
            return await CreateResponse(req, HttpStatusCode.OK, new SubmitResult
            {
                Success = !duplicate || !string.IsNullOrEmpty(ksefNumber),
                Duplicate = duplicate,
                Retryable = false,
                ElementReferenceNumber = sendResult.ElementReferenceNumber,
                KSeFReferenceNumber = ksefNumber,
                SessionToken = session.SessionToken,
                SessionReferenceNumber = session.SessionReferenceNumber,
                QRVerificationUrl = qrUrl,
                Error = duplicate && string.IsNullOrEmpty(ksefNumber)
                    ? "Already submitted to KSeF (duplicate); KSeF number not returned in this session — verify in the KSeF portal."
                    : null
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
    /// Look an invoice up directly in KSeF by seller NIP + invoice number (exact match) around its
    /// issue date. Recovers the KSeF number and QR verification URL for documents whose submit
    /// response was lost — needs no stored element/session references, and opens no online session.
    /// GET /api/invoice/lookup?nip={nip}&amp;invoiceNumber={no}&amp;issueDate={yyyy-MM-dd}
    /// </summary>
    [Function("LookupInvoice")]
    public async Task<HttpResponseData> LookupInvoice(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "invoice/lookup")] HttpRequestData req)
    {
        try
        {
            var nip = req.Query["nip"];
            var invoiceNumber = req.Query["invoiceNumber"];
            var issueDateText = req.Query["issueDate"];

            if (string.IsNullOrEmpty(nip) || string.IsNullOrEmpty(invoiceNumber) || string.IsNullOrEmpty(issueDateText))
                return await CreateResponse(req, HttpStatusCode.BadRequest,
                    new LookupResult { Success = false, Error = "Missing 'nip', 'invoiceNumber' or 'issueDate' query parameter" });

            if (!DateTime.TryParse(issueDateText, out var issueDate))
                return await CreateResponse(req, HttpStatusCode.BadRequest,
                    new LookupResult { Success = false, Error = $"Invalid issueDate '{issueDateText}' — expected yyyy-MM-dd" });

            _logger.LogInformation("LookupInvoice called for {Number} (issue date {Date})", invoiceNumber, issueDateText);

            // Small window around the issue date — the query filter is exact-match on invoice
            // number, the range only bounds the search.
            var meta = await _ksef.QueryInvoiceByNumberAsync(nip, invoiceNumber, issueDate.AddDays(-2), issueDate.AddDays(3));

            if (meta == null)
            {
                _logger.LogInformation("LookupInvoice: {Number} not found in KSeF", invoiceNumber);
                return await CreateResponse(req, HttpStatusCode.OK, new LookupResult { Success = true, Found = false });
            }

            // Same QR format the submit flow produces: /web/verify/{ksefNumber}/{sha256-hex-lower}.
            string? qrUrl = null;
            if (!string.IsNullOrEmpty(meta.InvoiceHash))
            {
                var hashHex = Convert.ToHexString(Convert.FromBase64String(meta.InvoiceHash)).ToLowerInvariant();
                qrUrl = $"{_ksef.BaseUrl.Replace("/v2", "")}/web/verify/{meta.KsefNumber}/{hashHex}";
            }

            _logger.LogInformation("LookupInvoice: {Number} found in KSeF as {KsefNumber}", invoiceNumber, meta.KsefNumber);
            return await CreateResponse(req, HttpStatusCode.OK, new LookupResult
            {
                Success = true,
                Found = true,
                KSeFReferenceNumber = meta.KsefNumber,
                QRVerificationUrl = qrUrl,
                AcquisitionTimestamp = meta.AcquisitionDate,
                InvoicingDate = meta.InvoicingDate,
                IssueDate = meta.IssueDate
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LookupInvoice failed");
            return await CreateResponse(req, HttpStatusCode.InternalServerError,
                new LookupResult { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Tests KSeF authentication for a NIP: performs the real auth handshake (InitSession) and
    /// terminates it, WITHOUT sending an invoice. Surfaces a bad/missing KSeF token — which the
    /// static /health check can't catch. GET /api/test-ksef-auth?nip={nip}
    /// </summary>
    [Function("TestKSeFAuth")]
    public async Task<HttpResponseData> TestKSeFAuth(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "test-ksef-auth")] HttpRequestData req)
    {
        var nip = req.Query["nip"];
        _logger.LogInformation("TestKSeFAuth called for NIP {NIP}", nip);

        if (string.IsNullOrEmpty(nip))
            return await CreateResponse(req, HttpStatusCode.BadRequest,
                new StatusResult { Success = false, Error = "Missing 'nip' query parameter" });

        try
        {
            // Real KSeF auth round-trip: encrypts the configured KSeF token, authenticates for the NIP,
            // then terminates. Throws if the token is missing/invalid or the NIP isn't authorised.
            var session = await _ksef.InitSessionAsync(nip);
            await _ksef.TerminateSessionAsync(session.SessionToken);
            return await CreateResponse(req, HttpStatusCode.OK, new StatusResult
            {
                Success = true,
                ProcessingDescription = $"KSeF authentication OK for NIP {nip} against {_ksef.BaseUrl}."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TestKSeFAuth failed for NIP {NIP}", nip);
            return await CreateResponse(req, HttpStatusCode.OK,
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
