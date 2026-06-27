using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AuctionSystem.Functions.Services;

/// <summary>
/// Enqueues a catalogue import (build the auction snapshot from the chosen Active catalogues, create the
/// auction.Lots rows, lock the catalogues) so the HTTP request returns immediately. Importing 2+ real-size
/// catalogues overruns the gateway timeout and the request is disposed mid-flight ("IFeatureCollection has
/// been disposed"). The worker (AuctionFunctions.CatalogImportWorker) runs it off the request thread; the
/// page polls the auction's SnapshotStatus. Message body is JSON: { AuctionId, CatalogIds }.
/// </summary>
public sealed class CatalogImportQueue
{
    public const string QueueName = "catalog-import";

    private readonly QueueClient? _queue;
    private readonly ILogger<CatalogImportQueue> _logger;

    public CatalogImportQueue(QueueClient? queue, ILogger<CatalogImportQueue> logger)
    {
        _logger = logger;
        _queue = queue;
    }

    /// <summary>When false the caller imports synchronously (no storage configured, e.g. local dev).</summary>
    public bool IsConfigured => _queue != null;

    public async Task EnqueueAsync(int auctionId, List<int> catalogIds)
    {
        if (_queue is null)
        {
            _logger.LogWarning("Catalog-import queue not configured; auction {Id} not enqueued", auctionId);
            return;
        }
        await _queue.CreateIfNotExistsAsync();
        await _queue.SendMessageAsync(JsonSerializer.Serialize(
            new CatalogImportMessage { AuctionId = auctionId, CatalogIds = catalogIds }));
    }
}

public sealed class CatalogImportMessage
{
    public int AuctionId { get; set; }
    public List<int> CatalogIds { get; set; } = new();
}
