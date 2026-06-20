using AuctionSystem.Domain.Data;
using AuctionSystem.Functions.BusinessCentral.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

/// <summary>
/// Business Central push drainer — strictly SERIAL, one document at a time, confirm-before-next.
///
/// BC's posting engine deadlocks under any concurrency (and even occasionally serial), so we do NOT
/// scale out: a single timer-triggered drainer (timer triggers are singleton — only one instance ever
/// fires) walks the unpushed documents ONE AT A TIME. Each push re-reads BC to confirm the document is
/// actually posted before moving to the next; an unconfirmed one stays "Not Posted" and is retried on
/// the next tick. No queue worker, so the same document is never pushed by two consumers at once — which
/// is what previously deadlocked the Sales Line table and produced empty/zero posts.
///
/// The push methods themselves are idempotent (DeleteStaleDraft + external-doc-number anchor) and retry
/// BC deadlocks at the whole-push level, so repeating a tick is always safe.
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

    // Resolved lazily so the drainer simply no-ops when BC isn't configured (the service is only
    // registered when BC settings are present), instead of failing to construct.
    private BusinessCentralSyncService? Sync => _services.GetService<BusinessCentralSyncService>();

    // Serial drainer. Runs every minute; the timer is singleton so only one instance ever drains, and
    // PushCreditNotesAsync/PushInvoicesAsync push one document at a time, confirming each in BC before
    // the next. Credit notes first so a take-back's reversal lands before its re-invoice.
    [Function("BcPushDrainer")]
    public async Task RunDrainer([TimerTrigger("0 */1 * * * *")] TimerInfo timer)
    {
        var sync = Sync;
        if (sync is null) return;

        if (await _db.Invoices.AnyAsync(i => i.IsCreditNote && (i.BcInvoiceNumber == null || i.BcInvoiceNumber == "")))
            await sync.PushCreditNotesAsync();
        if (await _db.Invoices.AnyAsync(i => !i.IsCreditNote && (i.BcInvoiceNumber == null || i.BcInvoiceNumber == "")))
            await sync.PushInvoicesAsync();

        // Apply posted-but-unapplied credit memos to their original invoices. The immediate apply at push
        // time can miss when BC hasn't surfaced the just-posted ledger entry yet, leaving the invoice open
        // (a residual buyer balance). Retry it here until the credit note is Alloted. Apply-only — does not
        // touch the document push/idempotency path. Idempotent (skips already-applied/closed credit memos).
        if (await _db.Invoices.AnyAsync(cn => cn.IsCreditNote && cn.BcInvoiceNumber != null && cn.BcInvoiceNumber != ""
                && cn.OriginalInvoiceId != null && cn.Status != AuctionSystem.Domain.Enums.InvoiceStatus.Alloted))
            await sync.ApplyPendingCreditMemosAsync();
    }
}
