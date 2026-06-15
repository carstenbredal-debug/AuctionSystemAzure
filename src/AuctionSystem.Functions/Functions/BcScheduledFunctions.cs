using AuctionSystem.Functions.BusinessCentral.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class BcScheduledFunctions
{
    private readonly BusinessCentralSyncService? _bcSync;
    private readonly ILogger<BcScheduledFunctions> _logger;

    public BcScheduledFunctions(ILogger<BcScheduledFunctions> logger, BusinessCentralSyncService? bcSync = null)
    {
        _logger = logger;
        _bcSync = bcSync;
    }

    /// <summary>
    /// Every 30 minutes: reconcile the local BcSyncedAt flags against BC so the sync-status
    /// counts on the Business Central tab stay accurate without manual refreshing.
    /// </summary>
    [Function("BcSyncStatusCheck")]
    public async Task Run([TimerTrigger("0 */30 * * * *")] TimerInfo timer)
    {
        if (_bcSync is null)
        {
            _logger.LogInformation("BC not configured; skipping scheduled sync-status check");
            return;
        }

        try
        {
            var changed = await _bcSync.ReconcileSyncStatusAsync();
            _logger.LogInformation("Scheduled BC sync-status check complete ({Changed} flag(s) corrected)", changed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled BC sync-status check failed");
        }
    }
}
