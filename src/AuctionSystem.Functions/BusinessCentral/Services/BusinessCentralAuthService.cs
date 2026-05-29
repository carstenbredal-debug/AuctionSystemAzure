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

    public async Task<string> GetAccessTokenAsync()
    {
        try
        {
            var result = await _msalClient
                .AcquireTokenForClient(Scopes)
                .ExecuteAsync();

            _logger.LogInformation("Acquired BC access token, expires {Expiry}", result.ExpiresOn);
            return result.AccessToken;
        }
        catch (MsalException ex)
        {
            _logger.LogError(ex, "Failed to acquire BC access token");
            throw;
        }
    }
}
