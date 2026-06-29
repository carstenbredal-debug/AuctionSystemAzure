using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Services;

/// <summary>
/// Enqueues a catalogue "freeze" (Activate) so the HTTP request returns immediately instead of blocking
/// on a SELECT ... INTO over a multi-million-row SkinTable (which overruns the gateway timeout and surfaces
/// as a false 500 even though the freeze succeeds). The queue worker (CatalogDraftFunctions.CatalogFreezeWorker)
/// runs FreezeCatalogAsync off the request thread and flips the draft Active when done. Message body = draft id.
/// </summary>
public sealed class CatalogFreezeQueue
{
    public const string QueueName = "catalog-freeze";

    private readonly QueueClient? _queue;
    private readonly ILogger<CatalogFreezeQueue> _logger;

    public CatalogFreezeQueue(QueueClient? queue, ILogger<CatalogFreezeQueue> logger)
    {
        _logger = logger;
        _queue = queue;
    }

    /// <summary>When false the caller should freeze synchronously (no storage configured, e.g. local dev).</summary>
    public bool IsConfigured => _queue != null;

    public async Task EnqueueAsync(int draftId)
    {
        if (_queue is null)
        {
            _logger.LogWarning("Catalog-freeze queue not configured; draft {Id} not enqueued", draftId);
            return;
        }
        await _queue.CreateIfNotExistsAsync();
        await _queue.SendMessageAsync(draftId.ToString());
    }
}
