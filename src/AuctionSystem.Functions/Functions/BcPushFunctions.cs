using System.Text.Json;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Functions.BusinessCentral.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

/// <summary>
/// Background Business Central push. Keeps BC off the user request thread so a slow or timing-out BC
/// can never hang a take-back or sale — the condition that previously led to re-clicks and duplicate
/// credit notes. The actual push reuses the existing idempotent push methods, so retries are safe.
/// </summary>
public class BcPushFunctions
{
    private readonly IServiceProvider _services;
    private readonly AuctionDbContext _db;
    private readonly ILogger<BcPushFunctions> _logger;

    public BcPushFunctions(IServiceProvider services, AuctionDbContext db, ILogger<BcPushFunctions> logger)
    {
        _services = services;
        _db = db;
        _logger = logger;
    }

    // Resolved lazily so the worker/sweep simply no-op when BC isn't configured (the service is only
    // registered when BC settings are present), instead of failing to construct.
    private BusinessCentralSyncService? Sync => _services.GetService<BusinessCentralSyncService>();

    [Function("BcPushWorker")]
    public async Task RunWorker(
        [QueueTrigger(BcPushQueue.QueueName, Connection = "AzureWebJobsStorage")] string message)
    {
        var sync = Sync;
        if (sync is null) { _logger.LogWarning("BC not configured; dropping push message {Message}", message); return; }

        var msg = JsonSerializer.Deserialize<BcPushMessage>(message);
        if (msg is null) { _logger.LogWarning("Unparseable BC push message: {Message}", message); return; }

        // A throw here lets the queue retry with back-off; after maxDequeueCount the message is
        // poison-queued and the still-unpushed document is recovered by the timer sweep below.
        if (msg.Type == BcPushQueue.CreditNote)
        {
            var cn = await LoadInvoiceAsync(msg.Id, isCreditNote: true);
            if (cn != null) await sync.PushCreditNoteToBcAsync(cn);
        }
        else if (msg.Type == BcPushQueue.Invoice)
        {
            var inv = await LoadInvoiceAsync(msg.Id, isCreditNote: false);
            if (inv != null) await sync.PushInvoiceToBcAsync(inv);
        }
        else
        {
            _logger.LogWarning("Unknown BC push message type: {Type}", msg.Type);
        }
    }

    // Safety net: re-push anything still unpushed — a poisoned queue message, an enqueue that failed,
    // or a BC outage that has since recovered. Idempotent push methods make this safe to repeat, and
    // it only does work when something is actually pending.
    [Function("BcPushSweep")]
    public async Task RunSweep([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        var sync = Sync;
        if (sync is null) return;

        if (await _db.Invoices.AnyAsync(i => i.IsCreditNote && (i.BcInvoiceNumber == null || i.BcInvoiceNumber == "")))
            await sync.PushCreditNotesAsync();
        if (await _db.Invoices.AnyAsync(i => !i.IsCreditNote && (i.BcInvoiceNumber == null || i.BcInvoiceNumber == "")))
            await sync.PushInvoicesAsync();
    }

    private Task<Invoice?> LoadInvoiceAsync(int id, bool isCreditNote) =>
        _db.Invoices
            .Include(i => i.Buyer)
            .Include(i => i.Broker)
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == id && i.IsCreditNote == isCreditNote);
}
