using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Services;

/// <summary>
/// Enqueues a paced broker-robot simulation so it runs server-side over time (mirrors TypistSimQueue).
/// The Start HTTP request returns immediately; the worker (AuctionResultFunctions.BrokerSimWorker) runs
/// one "pass" of broker activity, then re-enqueues a continuation (with a visibility delay = the pace)
/// until every sellable lot is sold or Stop is requested. Surviving page-close is the whole point: the
/// browser no longer drives the loop. The message body is the JSON-serialised run state (params + totals).
/// </summary>
public sealed class BrokerSimQueue
{
    public const string QueueName = "broker-sim";

    private readonly QueueClient? _queue;
    private readonly ILogger<BrokerSimQueue> _logger;

    public BrokerSimQueue(string? storageConnectionString, ILogger<BrokerSimQueue> logger)
    {
        _logger = logger;
        if (!string.IsNullOrEmpty(storageConnectionString) && storageConnectionString != "UseDevelopmentStorage=true")
        {
            _queue = new QueueClient(storageConnectionString, QueueName,
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        }
    }

    /// <summary>When false the caller should run synchronously (no storage configured, e.g. local dev).</summary>
    public bool IsConfigured => _queue != null;

    /// <summary>
    /// Enqueue the run message. An optional visibilityDelay paces the next pass — the message stays
    /// invisible for that long before the worker picks it up, so passes fire ~pace seconds apart without
    /// the worker blocking. Clamped to the queue's max (7 days; we only ever use seconds).
    /// </summary>
    public async Task EnqueueAsync(string messageJson, TimeSpan? visibilityDelay = null)
    {
        if (_queue is null)
        {
            _logger.LogWarning("Broker-sim queue not configured; run not enqueued");
            return;
        }
        await _queue.CreateIfNotExistsAsync();
        await _queue.SendMessageAsync(messageJson, visibilityTimeout: visibilityDelay);
    }
}
