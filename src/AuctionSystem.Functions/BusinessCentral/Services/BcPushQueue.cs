using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.BusinessCentral.Services;

public sealed record BcPushMessage(string Type, int Id);

/// <summary>
/// Enqueues a BC push so the user's request returns immediately instead of waiting on — and possibly
/// timing out against — Business Central. The queue worker (BcPushFunctions) does the actual push
/// with the existing idempotent push methods.
///
/// Degrades safely: if storage isn't configured or the enqueue fails, the document still exists
/// locally with no BcInvoiceNumber, so the timer sweep (BcPushFunctions.BcPushSweep) and the manual
/// admin "Sync" buttons will push it. The user request is never failed because of the queue.
/// </summary>
public sealed class BcPushQueue
{
    public const string QueueName = "bc-push";
    public const string CreditNote = "creditnote";
    public const string Invoice = "invoice";

    private readonly QueueClient? _queue;
    private readonly ILogger<BcPushQueue> _logger;

    public BcPushQueue(QueueClient? queue, ILogger<BcPushQueue> logger)
    {
        _logger = logger;
        _queue = queue;
    }

    public async Task EnqueueAsync(string type, int id)
    {
        if (_queue is null)
        {
            _logger.LogWarning("BC push queue not configured; {Type} {Id} will be pushed by the timer sweep / manual sync", type, id);
            return;
        }
        try
        {
            await _queue.CreateIfNotExistsAsync();
            await _queue.SendMessageAsync(JsonSerializer.Serialize(new BcPushMessage(type, id)));
        }
        catch (Exception ex)
        {
            // Never fail the user request because of the queue — the timer sweep recovers it.
            _logger.LogError(ex, "Failed to enqueue BC push for {Type} {Id}; relying on the timer sweep", type, id);
        }
    }
}
