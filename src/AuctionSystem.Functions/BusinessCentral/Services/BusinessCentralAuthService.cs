using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using AuctionSystem.Functions.BusinessCentral.Configuration;

namespace AuctionSystem.Functions.BusinessCentral.Services;

public class BusinessCentralAuthService
{
    private readonly IConfidentialClientApplication _msalClient;
    private readonly ILogger<BusinessCentralAuthService> _logger;

    private static readonly string[] Scopes =
        new[] { "https://api.businesscentral.dynamics.com/.default" };

    public BusinessCentralAuthService(
        IOptions<BusinessCentralOptions> options,
        ILogger<BusinessCentralAuthService> logger)
    {
        _logger = logger;
        var config = options.Value;

        _msalClient = ConfidentialClientApplicationBuilder
            .Create(config.ClientId)
            .WithClientSecret(config.ClientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{config.TenantId}"))
            .Build();
    }

    private const int MaxTokenAttempts = 3;

    public async Task<string> GetAccessTokenAsync()
    {
        // MSAL caches the app token in-memory and returns it until ~5 min before expiry, so this is
        // usually a no-op. The retry guards a transient AAD blip on the (infrequent) real fetch — the
        // BcRetryHandler can't cover this because token acquisition happens before the HTTP pipeline.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await _msalClient
                    .AcquireTokenForClient(Scopes)
                    .ExecuteAsync();

                _logger.LogDebug("Acquired BC access token, expires {Expiry}", result.ExpiresOn);
                return result.AccessToken;
            }
            catch (MsalException ex) when (attempt < MaxTokenAttempts && IsTransient(ex))
            {
                var delay = TimeSpan.FromMilliseconds(Math.Pow(2, attempt - 1) * 500);
                _logger.LogWarning(ex, "BC token acquisition failed (attempt {Attempt}/{Max}); retrying in {Delay}ms",
                    attempt, MaxTokenAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay);
            }
            catch (MsalException ex)
            {
                _logger.LogError(ex, "Failed to acquire BC access token");
                throw;
            }
        }
    }

    // Transient = AAD returned a server-side/throttling error or the request never completed; a fresh
    // attempt may succeed. A 4xx (e.g. bad client config) is not retried — it would just fail again.
    private static bool IsTransient(MsalException ex) =>
        ex is MsalServiceException svc
            ? svc.StatusCode is 0 or 429 or >= 500
            : ex.ErrorCode is "request_timeout" or "service_not_available" or "network_error";
}
