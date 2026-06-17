using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Services;

/// <summary>
/// Enqueues a per-auction snapshot build so the import HTTP request returns immediately instead of
/// blocking ~50s on the Lots/Boxes/Skins build (which exceeds the gateway timeout and surfaces as a
/// false failure even though the build succeeds). The queue worker (AuctionFunctions.SnapshotBuildWorker)
/// does the build off the request thread. The message body is just the auction id.
/// </summary>
public sealed class SnapshotBuildQueue
{
    public const string QueueName = "snapshot-build";

    private readonly QueueClient? _queue;
    private readonly ILogger<SnapshotBuildQueue> _logger;

    public SnapshotBuildQueue(string? storageConnectionString, ILogger<SnapshotBuildQueue> logger)
    {
        _logger = logger;
        if (!string.IsNullOrEmpty(storageConnectionString) && storageConnectionString != "UseDevelopmentStorage=true")
        {
            // Base64 matches the Functions queue-trigger encoding pinned in host.json.
            _queue = new QueueClient(storageConnectionString, QueueName,
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        }
    }

    /// <summary>When false the caller should build synchronously (no storage configured, e.g. local dev).</summary>
    public bool IsConfigured => _queue != null;

    public async Task EnqueueAsync(int auctionId)
    {
        if (_queue is null)
        {
            _logger.LogWarning("Snapshot build queue not configured; auction {Id} not enqueued", auctionId);
            return;
        }
        await _queue.CreateIfNotExistsAsync();
        await _queue.SendMessageAsync(auctionId.ToString());
    }
}
