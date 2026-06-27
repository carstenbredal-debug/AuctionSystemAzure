using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Services;

/// <summary>
/// Enqueues a paced typist-simulation run so it executes off the request thread over time. The
/// simulate HTTP request returns immediately; the worker (TypistEntryFunctions.TypistSimWorker) types
/// lots at a configurable delay and re-enqueues a continuation until the auction is fully typed (so a
/// full ~5000-lot run, which would take far longer than any request/function timeout, completes). The
/// message body is the JSON-serialised run parameters.
/// </summary>
public sealed class TypistSimQueue
{
    public const string QueueName = "typist-sim";

    private readonly QueueClient? _queue;
    private readonly ILogger<TypistSimQueue> _logger;

    public TypistSimQueue(QueueClient? queue, ILogger<TypistSimQueue> logger)
    {
        _logger = logger;
        _queue = queue;
    }

    /// <summary>When false the caller should run synchronously (no storage configured, e.g. local dev).</summary>
    public bool IsConfigured => _queue != null;

    public async Task EnqueueAsync(string messageJson)
    {
        if (_queue is null)
        {
            _logger.LogWarning("Typist-sim queue not configured; run not enqueued");
            return;
        }
        await _queue.CreateIfNotExistsAsync();
        await _queue.SendMessageAsync(messageJson);
    }
}
