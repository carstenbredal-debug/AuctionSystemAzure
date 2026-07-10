using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Services;

// Plain SMTP mailer, config-driven per environment:
//   SMTP_HOST, SMTP_PORT (default 587), SMTP_USER, SMTP_PASSWORD, SMTP_FROM (default SMTP_USER).
// Not configured -> IsConfigured is false and callers skip sending (with their own warning).
public class EmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string? Get(string key) => _configuration[key] ?? _configuration[$"Values:{key}"];

    public bool IsConfigured => !string.IsNullOrEmpty(Get("SMTP_HOST"));

    public async Task SendAsync(string to, string subject, string body, params (string FileName, byte[] Content)[] attachments)
    {
        var host = Get("SMTP_HOST") ?? throw new InvalidOperationException("SMTP_HOST is not configured.");
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
            _logger.LogInformation("Email '{Subject}' sent to {To} ({Attachments} attachment(s))", subject, to, attachments.Length);
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }
}
