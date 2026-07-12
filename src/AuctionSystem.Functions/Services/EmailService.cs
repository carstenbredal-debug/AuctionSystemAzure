using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Services;

// Mailer with two transports, picked by configuration:
//
// 1. Microsoft Graph sendMail (OAuth client credentials — works with M365 where basic SMTP auth is
//    disabled). App settings: GRAPH_MAIL_TENANT_ID, GRAPH_MAIL_CLIENT_ID, GRAPH_MAIL_CLIENT_SECRET,
//    GRAPH_MAIL_SENDER (the mailbox UPN the mail is sent as). The app registration needs the
//    Microsoft Graph APPLICATION permission Mail.Send with admin consent.
// 2. Plain SMTP fallback: SMTP_HOST, SMTP_PORT (default 587), SMTP_USER, SMTP_PASSWORD, SMTP_FROM.
//
// Neither configured -> IsConfigured false and callers skip sending with their own warning.
public class EmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;
    private static readonly HttpClient Http = new();

    private string? _cachedToken;
    private DateTime _tokenExpiresUtc = DateTime.MinValue;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string? Get(string key) => _configuration[key] ?? _configuration[$"Values:{key}"];

    private bool GraphConfigured =>
        !string.IsNullOrEmpty(Get("GRAPH_MAIL_TENANT_ID")) &&
        !string.IsNullOrEmpty(Get("GRAPH_MAIL_CLIENT_ID")) &&
        !string.IsNullOrEmpty(Get("GRAPH_MAIL_CLIENT_SECRET")) &&
        !string.IsNullOrEmpty(Get("GRAPH_MAIL_SENDER"));

    private bool SmtpConfigured => !string.IsNullOrEmpty(Get("SMTP_HOST"));

    public bool IsConfigured => GraphConfigured || SmtpConfigured;

    public async Task SendAsync(string to, string subject, string body, params (string FileName, byte[] Content)[] attachments)
    {
        if (GraphConfigured)
            await SendViaGraphAsync(to, subject, body, attachments);
        else
            await SendViaSmtpAsync(to, subject, body, attachments);
    }

    // ===== Microsoft Graph (OAuth) =====

    private async Task<string> GetGraphTokenAsync()
    {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiresUtc.AddMinutes(-5))
            return _cachedToken;

        var tenantId = Get("GRAPH_MAIL_TENANT_ID")!;
        var tokenResp = await Http.PostAsync(
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Get("GRAPH_MAIL_CLIENT_ID")!,
                ["client_secret"] = Get("GRAPH_MAIL_CLIENT_SECRET")!,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            }));
        var tokenBody = await tokenResp.Content.ReadAsStringAsync();
        if (!tokenResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Graph token request failed ({(int)tokenResp.StatusCode}): {tokenBody}");

        using var doc = JsonDocument.Parse(tokenBody);
        _cachedToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);
        return _cachedToken;
    }

    private async Task SendViaGraphAsync(string to, string subject, string body, (string FileName, byte[] Content)[] attachments)
    {
        var sender = Get("GRAPH_MAIL_SENDER")!;
        var token = await GetGraphTokenAsync();

        var message = new
        {
            message = new
            {
                subject,
                body = new { contentType = "Text", content = body },
                toRecipients = to.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(a => new { emailAddress = new { address = a } }).ToArray(),
                attachments = attachments.Select(a => new Dictionary<string, object>
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment",
                    ["name"] = a.FileName,
                    ["contentType"] = "application/pdf",
                    ["contentBytes"] = Convert.ToBase64String(a.Content)
                }).ToArray()
            },
            saveToSentItems = true
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(sender)}/sendMail")
        {
            Content = new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await Http.SendAsync(request);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Graph sendMail failed ({(int)resp.StatusCode}): {err}");
        }
        _logger.LogInformation("Email '{Subject}' sent to {To} via Graph as {Sender} ({Attachments} attachment(s))",
            subject, to, sender, attachments.Length);
    }

    // ===== SMTP fallback =====

    private async Task SendViaSmtpAsync(string to, string subject, string body, (string FileName, byte[] Content)[] attachments)
    {
        var host = Get("SMTP_HOST") ?? throw new InvalidOperationException("Neither Graph mail nor SMTP is configured.");
        var port = int.TryParse(Get("SMTP_PORT"), out var p) ? p : 587;
        var user = Get("SMTP_USER");
        var password = Get("SMTP_PASSWORD");
        var from = Get("SMTP_FROM") ?? user ?? throw new InvalidOperationException("SMTP_FROM / SMTP_USER is not configured.");

        using var message = new MailMessage { From = new MailAddress(from), Subject = subject, Body = body };
        foreach (var addr in to.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            message.To.Add(addr);

        var streams = new List<MemoryStream>();
        try
        {
            foreach (var (fileName, content) in attachments)
            {
                var stream = new MemoryStream(content);
                streams.Add(stream);
                message.Attachments.Add(new Attachment(stream, fileName, "application/pdf"));
            }

            using var client = new SmtpClient(host, port) { EnableSsl = true };
            if (!string.IsNullOrEmpty(user))
                client.Credentials = new NetworkCredential(user, password);

            await client.SendMailAsync(message);
            _logger.LogInformation("Email '{Subject}' sent to {To} via SMTP ({Attachments} attachment(s))", subject, to, attachments.Length);
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }
}
