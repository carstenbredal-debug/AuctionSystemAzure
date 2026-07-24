using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using KSeF.Functions.Models;

namespace KSeF.Functions.Services;

/// <summary>
/// Client for the KSeF 2.0 (Krajowy System e-Faktur) REST API.
/// Implements the v2 authentication, session management, and invoice
/// submission flow with RSA-encrypted tokens and AES-encrypted invoices.
/// </summary>
public class KSeFApiClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<KSeFApiClient> _logger;
    private readonly JsonSerializerOptions _jsonOpts;

    private string? _accessToken;
    private string? _sessionReferenceNumber;
    private byte[]? _aesKey;
    private byte[]? _aesIv;

    public KSeFApiClient(HttpClient http, IConfiguration config, ILogger<KSeFApiClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public string BaseUrl => _config["KSeF:BaseUrl"] ?? "https://api-demo.ksef.mf.gov.pl/v2";
    private string Token => _config["KSeF:Token"] ?? "";

    // KSeF tokens are bound to the context (NIP) they were generated in. Our default token
    // authenticates in OUR company's context; a self-invoicing session runs in the SUPPLIER's
    // context and needs a token generated there (possible once the supplier granted us the
    // samofakturowanie permission). Config: KSeF__TokenByNip__{nip} app setting per supplier,
    // falling back to the default token.
    private string TokenFor(string nip)
    {
        var perNip = _config[$"KSeF:TokenByNip:{nip}"];
        return string.IsNullOrEmpty(perNip) ? Token : perNip;
    }

    /// <summary>
    /// Authenticate with KSeF v2 using a KSeF token.
    /// 1. POST /auth/challenge
    /// 2. GET /security/public-key-certificates
    /// 3. Encrypt token with RSA-OAEP
    /// 4. POST /auth/ksef-token
    /// 5. Poll GET /auth/{ref} until accessToken is ready
    /// </summary>
    public async Task<KSeFSessionResponse> InitSessionAsync(string nip)
    {
        _logger.LogInformation("KSeF v2 auth: starting for NIP {NIP} against {BaseUrl}", nip, BaseUrl);

        if (string.IsNullOrEmpty(TokenFor(nip)))
            throw new Exception("KSeF token not configured. Set the KSeF__Token environment variable on the Function App.");

        // Step 1: Get challenge
        var challengeResp = await _http.PostAsync($"{BaseUrl}/auth/challenge", null);
        var challengeBody = await challengeResp.Content.ReadAsStringAsync();
        if (!challengeResp.IsSuccessStatusCode)
            throw new Exception($"KSeF auth challenge failed: {challengeResp.StatusCode} - {TruncateForLog(challengeBody)}");

        using var challengeDoc = JsonDocument.Parse(challengeBody);
        var challenge = challengeDoc.RootElement.GetProperty("challenge").GetString()!;
        var timestampMs = challengeDoc.RootElement.GetProperty("timestampMs").GetInt64();
        _logger.LogInformation("KSeF challenge obtained: {Challenge}", challenge);

        // Step 2: Get encryption certificate
        var certsResp = await _http.GetAsync($"{BaseUrl}/security/public-key-certificates");
        var certsBody = await certsResp.Content.ReadAsStringAsync();
        if (!certsResp.IsSuccessStatusCode)
            throw new Exception($"KSeF cert fetch failed: {certsResp.StatusCode} - {certsBody}");

        using var certsDoc = JsonDocument.Parse(certsBody);
        string? tokenCertB64 = null;
        string? tokenPublicKeyId = null;
        string? symmetricCertB64 = null;
        string? symmetricPublicKeyId = null;
        string latestTokenValidFrom = "";
        string latestSymValidFrom = "";

        foreach (var cert in certsDoc.RootElement.EnumerateArray())
        {
            var usages = cert.GetProperty("usage");
            foreach (var u in usages.EnumerateArray())
            {
                var usage = u.GetString();
                if (usage == "KsefTokenEncryption")
                {
                    var vf = cert.GetProperty("validFrom").GetString() ?? "";
                    if (string.Compare(vf, latestTokenValidFrom, StringComparison.Ordinal) > 0)
                    {
                        latestTokenValidFrom = vf;
                        tokenCertB64 = cert.GetProperty("certificate").GetString();
                        tokenPublicKeyId = cert.GetProperty("publicKeyId").GetString();
                    }
                }
                if (usage == "SymmetricKeyEncryption")
                {
                    var vf = cert.GetProperty("validFrom").GetString() ?? "";
                    if (string.Compare(vf, latestSymValidFrom, StringComparison.Ordinal) > 0)
                    {
                        latestSymValidFrom = vf;
                        symmetricCertB64 = cert.GetProperty("certificate").GetString();
                        symmetricPublicKeyId = cert.GetProperty("publicKeyId").GetString();
                    }
                }
            }
        }

        if (tokenCertB64 == null || tokenPublicKeyId == null)
            throw new Exception("No KsefTokenEncryption certificate found");
        if (symmetricCertB64 == null || symmetricPublicKeyId == null)
            throw new Exception("No SymmetricKeyEncryption certificate found");

        // Step 3: Encrypt token|timestampMs with RSA-OAEP SHA-256
        var contextToken = TokenFor(nip);
        if (contextToken != Token)
            _logger.LogInformation("KSeF auth: using per-context token for NIP {NIP}", nip);
        var payload = $"{contextToken}|{timestampMs}";
        var certDer = Convert.FromBase64String(tokenCertB64);
        var x509 = new X509Certificate2(certDer);
        var rsa = x509.GetRSAPublicKey()
            ?? throw new Exception("Certificate does not have an RSA public key");
        var encryptedToken = rsa.Encrypt(
            Encoding.UTF8.GetBytes(payload),
            RSAEncryptionPadding.OaepSHA256);
        var encryptedTokenB64 = Convert.ToBase64String(encryptedToken);

        // Step 4: POST /auth/ksef-token
        var authBody = new
        {
            challenge,
            contextIdentifier = new { type = "nip", value = nip },
            encryptedToken = encryptedTokenB64,
            publicKeyId = tokenPublicKeyId
        };
        var authJson = JsonSerializer.Serialize(authBody, _jsonOpts);
        var authResp = await _http.PostAsync($"{BaseUrl}/auth/ksef-token",
            new StringContent(authJson, Encoding.UTF8, "application/json"));
        var authRespBody = await authResp.Content.ReadAsStringAsync();

        if (authResp.StatusCode != System.Net.HttpStatusCode.Accepted &&
            authResp.StatusCode != System.Net.HttpStatusCode.OK)
            throw new Exception($"KSeF token auth failed: {authResp.StatusCode} - {authRespBody}");

        using var authDoc = JsonDocument.Parse(authRespBody);
        var referenceNumber = authDoc.RootElement.GetProperty("referenceNumber").GetString()!;
        var authenticationToken = authDoc.RootElement
            .GetProperty("authenticationToken")
            .GetProperty("token").GetString()!;

        _logger.LogInformation("KSeF auth submitted, ref: {Ref}", referenceNumber);

        // Step 5: Poll GET /auth/{ref} until auth is confirmed
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(3000);
            var statusReq = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/auth/{referenceNumber}");
            statusReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authenticationToken);
            var statusResp = await _http.SendAsync(statusReq);
            var statusBody = await statusResp.Content.ReadAsStringAsync();

            using var statusDoc = JsonDocument.Parse(statusBody);
            if (statusDoc.RootElement.TryGetProperty("status", out var status))
            {
                var code = status.GetProperty("code").GetInt32();
                if (code == 200)
                {
                    _logger.LogInformation("KSeF auth confirmed");
                    break;
                }
                if (code >= 400)
                {
                    var desc = status.TryGetProperty("description", out var d) ? d.GetString() : "unknown";
                    var details = status.TryGetProperty("details", out var det)
                        ? string.Join("; ", det.EnumerateArray().Select(x => x.GetString()))
                        : "";
                    throw new Exception($"KSeF auth failed (code {code}): {desc}. {details}");
                }
            }
        }

        // Step 6: Redeem authentication token for access token with permissions
        var redeemReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/auth/token/redeem");
        redeemReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authenticationToken);
        var redeemResp = await _http.SendAsync(redeemReq);
        var redeemBody = await redeemResp.Content.ReadAsStringAsync();

        if (!redeemResp.IsSuccessStatusCode)
            throw new Exception($"KSeF token redeem failed: {redeemResp.StatusCode} - {TruncateForLog(redeemBody)}");

        using var redeemDoc = JsonDocument.Parse(redeemBody);
        _accessToken = redeemDoc.RootElement
            .GetProperty("accessToken")
            .GetProperty("token").GetString()
            ?? throw new Exception("KSeF token redeem returned no access token");

        _logger.LogInformation("KSeF access token obtained with permissions");

        // Step 7: Open interactive session
        _aesKey = RandomNumberGenerator.GetBytes(32);
        _aesIv = RandomNumberGenerator.GetBytes(16);

        var symCertDer = Convert.FromBase64String(symmetricCertB64);
        var symX509 = new X509Certificate2(symCertDer);
        var symRsa = symX509.GetRSAPublicKey()
            ?? throw new Exception("Symmetric certificate does not have an RSA public key");
        var encryptedKey = symRsa.Encrypt(_aesKey, RSAEncryptionPadding.OaepSHA256);

        var sessionBody = new
        {
            formCode = new
            {
                // FA(3) is mandatory from 2026-02-01. Must match the schema emitted by InvoiceXmlBuilder.
                systemCode = "FA (3)",
                schemaVersion = "1-0E",
                value = "FA"
            },
            encryption = new
            {
                encryptedSymmetricKey = Convert.ToBase64String(encryptedKey),
                initializationVector = Convert.ToBase64String(_aesIv),
                publicKeyId = symmetricPublicKeyId
            }
        };
        var sessionJson = JsonSerializer.Serialize(sessionBody, _jsonOpts);
        var sessionReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/sessions/online")
        {
            Content = new StringContent(sessionJson, Encoding.UTF8, "application/json")
        };
        sessionReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var sessionResp = await _http.SendAsync(sessionReq);
        var sessionRespBody = await sessionResp.Content.ReadAsStringAsync();

        if (!sessionResp.IsSuccessStatusCode)
        {
            _logger.LogError("KSeF session open failed: {Status} {Body}", sessionResp.StatusCode, sessionRespBody);
            throw KSeFException.FromResponse("KSeF session open failed", sessionResp.StatusCode, sessionRespBody);
        }

        using var sessionDoc = JsonDocument.Parse(sessionRespBody);
        _sessionReferenceNumber = sessionDoc.RootElement
            .GetProperty("referenceNumber").GetString()!;

        _logger.LogInformation("KSeF session opened: {Ref}", _sessionReferenceNumber);

        return new KSeFSessionResponse
        {
            SessionToken = _accessToken,
            SessionReferenceNumber = _sessionReferenceNumber,
            Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Send an encrypted invoice XML within an active v2 session.
    /// </summary>
    public async Task<KSeFSendResponse> SendInvoiceAsync(string invoiceXml, string? sessionToken = null)
    {
        var token = sessionToken ?? _accessToken
            ?? throw new InvalidOperationException("No active KSeF session. Call InitSessionAsync first.");
        var sessionRef = _sessionReferenceNumber
            ?? throw new InvalidOperationException("No active KSeF session reference.");

        if (_aesKey == null || _aesIv == null)
            throw new InvalidOperationException("No encryption keys. Call InitSessionAsync first.");

        var invoiceBytes = Encoding.UTF8.GetBytes(invoiceXml);

        // Compute hashes on plain invoice
        var invoiceHash = Convert.ToBase64String(SHA256.HashData(invoiceBytes));
        var invoiceSize = invoiceBytes.Length;

        // Encrypt with AES-256-CBC
        byte[] encryptedInvoice;
        using (var aes = Aes.Create())
        {
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = _aesKey;
            aes.IV = _aesIv;
            using var encryptor = aes.CreateEncryptor();
            encryptedInvoice = encryptor.TransformFinalBlock(invoiceBytes, 0, invoiceBytes.Length);
        }

        var encryptedInvoiceHash = Convert.ToBase64String(SHA256.HashData(encryptedInvoice));
        var encryptedInvoiceSize = encryptedInvoice.Length;

        var sendBody = new
        {
            invoiceHash,
            invoiceSize,
            encryptedInvoiceHash,
            encryptedInvoiceSize,
            encryptedInvoiceContent = Convert.ToBase64String(encryptedInvoice)
        };

        var sendJson = JsonSerializer.Serialize(sendBody, _jsonOpts);
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{BaseUrl}/sessions/online/{sessionRef}/invoices/")
        {
            Content = new StringContent(sendJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        _logger.LogInformation("Sending encrypted invoice to KSeF session {Ref}", sessionRef);

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("KSeF invoice send failed: {Status} {Body}", response.StatusCode, responseBody);
            throw KSeFException.FromResponse("KSeF invoice send failed", response.StatusCode, responseBody);
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        return new KSeFSendResponse
        {
            ElementReferenceNumber = root.GetProperty("referenceNumber").GetString() ?? "",
            ProcessingTimestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Check the status of a submitted invoice using the original session reference.
    /// Uses GET /sessions/{sessionRef}/invoices/{invoiceRef} endpoint.
    /// </summary>
    public async Task<KSeFInvoiceStatus> GetInvoiceStatusBySessionAsync(
        string sessionReferenceNumber, string invoiceReferenceNumber, string? sessionToken = null)
    {
        var token = sessionToken ?? _accessToken
            ?? throw new InvalidOperationException("No active KSeF session.");

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{BaseUrl}/sessions/{sessionReferenceNumber}/invoices/{invoiceReferenceNumber}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        _logger.LogInformation("Checking KSeF invoice status for {Ref} in session {Session}",
            invoiceReferenceNumber, sessionReferenceNumber);

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("KSeF status check failed: {Status} {Body}", response.StatusCode, responseBody);
            throw KSeFException.FromResponse("KSeF status check failed", response.StatusCode, responseBody);
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var statusCode = 0;
        string? statusDesc = null;
        List<string>? details = null;
        if (root.TryGetProperty("status", out var statusObj))
        {
            statusCode = statusObj.TryGetProperty("code", out var sc) ? sc.GetInt32() : 0;
            statusDesc = statusObj.TryGetProperty("description", out var sd) ? sd.GetString() : null;
            if (statusObj.TryGetProperty("details", out var detailsArr) && detailsArr.ValueKind == JsonValueKind.Array)
            {
                details = new List<string>();
                foreach (var d in detailsArr.EnumerateArray())
                    details.Add(d.GetString() ?? "");
            }
        }

        return new KSeFInvoiceStatus
        {
            ElementReferenceNumber = invoiceReferenceNumber,
            ProcessingCode = statusCode,
            ProcessingDescription = statusDesc,
            Details = details,
            KSeFReferenceNumber = root.TryGetProperty("ksefNumber", out var kn) ? kn.GetString() : null,
            AcquisitionTimestamp = root.TryGetProperty("acquisitionTimestamp", out var at) ? at.GetDateTime() : null
        };
    }

    private static string TruncateForLog(string text, int maxLen = 300)
    {
        if (text.Length <= maxLen) return text;
        return text[..maxLen] + "...";
    }

    /// <summary>
    /// Close the active KSeF interactive session.
    /// </summary>
    public async Task TerminateSessionAsync(string? sessionToken = null)
    {
        var token = sessionToken ?? _accessToken;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(_sessionReferenceNumber))
            return;

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{BaseUrl}/sessions/online/{_sessionReferenceNumber}/close");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        _logger.LogInformation("Closing KSeF session {Ref}", _sessionReferenceNumber);

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("KSeF session close failed: {Status} {Body}", response.StatusCode, body);
        }

        _accessToken = null;
        _sessionReferenceNumber = null;
        _aesKey = null;
        _aesIv = null;
    }
}
