using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Functions.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class TypistEntryFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly ILogger<TypistEntryFunctions> _logger;
    private readonly Services.TypistSimQueue _simQueue;
    private readonly IConfiguration _configuration;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TypistEntryFunctions(AuctionDbContext db, CatalogDbContext catalogDb, ILogger<TypistEntryFunctions> logger, Services.TypistSimQueue simQueue, IConfiguration configuration)
    {
        _db = db;
        _catalogDb = catalogDb;
        _logger = logger;
        _simQueue = simQueue;
        _configuration = configuration;
    }

    [AuctionSystem.Functions.Auth.RequireRole("Typist", "Admin")]
    [Function("SubmitTypistEntry")]
    public async Task<HttpResponseData> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "typist-entries")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SubmitTypistEntryRequest>();
        if (body == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        // The typist is the authenticated user — never trust a TypistUserId supplied in the body.
        var authUserId = req.FunctionContext.GetAppUser()?.Id ?? 0;
        if (authUserId == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);
        body = body with { TypistUserId = authUserId };

        var broker = await _db.Brokers.FindAsync(body.BrokerId);
        if (broker == null)
            return await CreateErrorResponse(req, "Broker not found");

        var typistUser = await _db.AppUsers.FindAsync(body.TypistUserId);
        if (typistUser == null)
            return await CreateErrorResponse(req, "Typist user not found");

        // Enforce max 2 unique typists per auction
        if (body.AuctionId > 0)
        {
            var activeTypistIds = await _db.TypistEntries
                .Where(e => e.AuctionId == body.AuctionId && !e.IsResolved && !e.IsMatched)
                .Select(e => e.TypistUserId)
                .Distinct()
                .ToListAsync();

            if (activeTypistIds.Count >= 2 && !activeTypistIds.Contains(body.TypistUserId))
                return await CreateErrorResponse(req, "This auction already has 2 active typists. Only 2 typists can work on an auction at a time.");
        }

        // Check if this typist already submitted for this lot (and not resolved)
        var existingEntry = await _db.TypistEntries
            .Where(e => e.LotNumber == body.LotNumber && e.TypistUserId == body.TypistUserId
                        && !e.IsResolved && !e.IsDisagreement)
            .FirstOrDefaultAsync();

        if (existingEntry != null)
            return await CreateErrorResponse(req, "You have already submitted an entry for this lot");

        // Determine typist slot (1 or 2) based on existing entries for this lot
        var existingEntries = await _db.TypistEntries
            .Where(e => e.LotNumber == body.LotNumber && !e.IsResolved && !e.IsDisagreement)
            .ToListAsync();

        if (existingEntries.Count >= 2)
            return await CreateErrorResponse(req, "Both typist entries already submitted for this lot");

        var slot = existingEntries.Count == 0 ? 1 : 2;

        var entry = new TypistEntry
        {
            LotNumber = body.LotNumber,
            AuctionId = body.AuctionId,
            BrokerId = body.BrokerId,
            PriceEur = body.PriceEur,
            TypistUserId = body.TypistUserId,
            TypistSlot = slot,
            EnteredAt = DateTime.UtcNow
        };

        _db.TypistEntries.Add(entry);
        await _db.SaveChangesAsync();

        // Compare once BOTH typists have entered. Re-query AFTER our insert (not the pre-insert slot
        // count) so two simultaneous first entries can't both get "slot 1" and miss the comparison;
        // only the higher-Id entry compares, so concurrent requests can't double-resolve.
        var otherEntry = await _db.TypistEntries
            .Where(e => e.LotNumber == body.LotNumber && e.TypistUserId != entry.TypistUserId
                        && !e.IsResolved && !e.IsDisagreement && !e.IsMatched)
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();

        if (otherEntry != null && entry.Id > otherEntry.Id)
            await CompareEntries(entry, otherEntry);

        return await CreateJsonResponse(req, new
        {
            entryId = entry.Id,
            lotNumber = entry.LotNumber,
            slot,
            isMatched = entry.IsMatched,
            isDisagreement = entry.IsDisagreement,
            waitingForOtherTypist = slot == 1
        });
    }

    [AuctionSystem.Functions.Auth.RequireRole("Typist", "Admin")]
    [Function("SubmitDisagreementReentry")]
    public async Task<HttpResponseData> SubmitReentry(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "typist-entries/reentry")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SubmitTypistEntryRequest>();
        if (body == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        // The typist is the authenticated user — never trust a TypistUserId supplied in the body.
        var authUserId = req.FunctionContext.GetAppUser()?.Id ?? 0;
        if (authUserId == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);
        body = body with { TypistUserId = authUserId };

        var broker = await _db.Brokers.FindAsync(body.BrokerId);
        if (broker == null)
            return await CreateErrorResponse(req, "Broker not found");

        // Check if this typist already re-entered for this lot (pending reentry, not yet compared)
        var existingReentry = await _db.TypistEntries
            .Where(e => e.LotNumber == body.LotNumber && e.TypistUserId == body.TypistUserId
                        && !e.IsResolved && !e.IsDisagreement && !e.IsMatched)
            .FirstOrDefaultAsync();

        if (existingReentry != null)
            return await CreateErrorResponse(req, "You have already re-entered for this lot. Waiting for the other typist.");

        // Count existing reentries from the OTHER typist for this lot
        var existingReentries = await _db.TypistEntries
            .Where(e => e.LotNumber == body.LotNumber && !e.IsResolved && !e.IsDisagreement && !e.IsMatched)
            .ToListAsync();

        var slot = existingReentries.Count == 0 ? 1 : 2;

        // The reentry request doesn't carry the auction; inherit it from the most recent prior
        // entry for this lot that has one set. Without this the resolved AuctionResult would be
        // created with AuctionId = 0 and never appear under its auction in the broker/buyer views.
        var auctionId = await _db.TypistEntries
            .Where(e => e.LotNumber == body.LotNumber && e.AuctionId > 0)
            .OrderByDescending(e => e.Id)
            .Select(e => e.AuctionId)
            .FirstOrDefaultAsync();

        var entry = new TypistEntry
        {
            LotNumber = body.LotNumber,
            AuctionId = auctionId,
            BrokerId = body.BrokerId,
            PriceEur = body.PriceEur,
            TypistUserId = body.TypistUserId,
            TypistSlot = slot,
            EnteredAt = DateTime.UtcNow
        };

        _db.TypistEntries.Add(entry);
        await _db.SaveChangesAsync();

        // Resolve when re-entries exist from BOTH typists. Re-query AFTER our insert instead of
        // trusting the pre-insert slot count — otherwise two simultaneous re-entries both see zero
        // existing and both get "slot 1", so neither triggers the comparison and the lot gets stuck.
        // Only the higher-Id entry resolves, so concurrent requests can't double-resolve.
        var otherTypistEntry = await _db.TypistEntries
            .Where(e => e.LotNumber == body.LotNumber && e.TypistUserId != entry.TypistUserId
                        && !e.IsResolved && !e.IsDisagreement && !e.IsMatched)
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();

        if (otherTypistEntry != null && entry.Id > otherTypistEntry.Id)
        {
            // Mark the old disagreement rows resolved (single writer — only this request gets here).
            var oldEntries = await _db.TypistEntries
                .Where(e => e.LotNumber == body.LotNumber && e.IsDisagreement && !e.IsResolved)
                .ToListAsync();
            foreach (var old in oldEntries)
                old.IsResolved = true;
            await _db.SaveChangesAsync();

            await CompareEntries(entry, otherTypistEntry);
        }

        return await CreateJsonResponse(req, new
        {
            entryId = entry.Id,
            lotNumber = entry.LotNumber,
            slot,
            isMatched = entry.IsMatched,
            isDisagreement = entry.IsDisagreement,
            waitingForOtherTypist = slot == 1
        });
    }

    private record SimulateRequest(int AuctionId, int? DisagreementPercent, int? MaxLots,
        int? TypistUserId1, int? TypistUserId2, decimal? MinPrice, decimal? MaxPrice, string? DisagreementType,
        List<int>? BrokerIds, int? DelaySeconds);

    // Queue message for the background paced run. Carries the run parameters; the worker recomputes the
    // remaining un-typed lots each invocation (so it's resumable / idempotent) and re-enqueues until done.
    private record TypistSimMessage(int AuctionId, int? DisagreementPercent, decimal? MinPrice, decimal? MaxPrice,
        string? DisagreementType, List<int>? BrokerIds, int? TypistUserId1, int? TypistUserId2, int DelaySeconds, int TotalTarget);

    // The two typist users a simulate/resolve run acts as: the supplied pair, else the first two active
    // Typist users. Null if fewer than two are available.
    private async Task<(AppUser A, AppUser B)?> ResolveSimTypistsAsync(int? id1, int? id2)
    {
        List<AppUser> typists;
        if (id1 is int t1 && id2 is int t2 && t1 != t2)
            typists = await _db.AppUsers.Where(u => u.Id == t1 || u.Id == t2).ToListAsync();
        else
            typists = await _db.AppUsers.Where(u => u.IsActive && u.Role == "Typist")
                .OrderBy(u => u.Id).Take(2).ToListAsync();
        return typists.Count >= 2 ? (typists[0], typists[1]) : null;
    }

    // TEST TOOL (admin-only): simulate a full typist pass for an auction — types each unsold lot as
    // two typists and drives the real matching (CompareEntries), so matched lots create AuctionResults
    // and a configurable share land in the disagreement queue. Bypasses the auth-derived TypistUserId
    // of SubmitTypistEntry because it must act as two users at once.
    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("SimulateTypistEntries")]
    public async Task<HttpResponseData> Simulate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "typist-entries/simulate")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SimulateRequest>();
        if (body == null || body.AuctionId <= 0)
            return await CreateErrorResponse(req, "auctionId is required");

        var auction = await _db.Auctions.FindAsync(body.AuctionId);
        if (auction == null)
            return await CreateErrorResponse(req, "Auction not found.");

        // One paced run per auction at a time — a second start would race the first and double-type lots
        // (extra entries/results). Wait until the status shows Done/Failed before starting another.
        if (auction.TypistSimStatus is string st && (st.StartsWith("Queued") || st.StartsWith("Typing")))
            return await CreateErrorResponse(req, $"A typist simulation is already running for this auction ({st}). Wait for it to finish before starting another.");

        var pair = await ResolveSimTypistsAsync(body.TypistUserId1, body.TypistUserId2);
        if (pair == null)
            return await CreateErrorResponse(req, "Need two Typist users (seed two, or pass typistUserId1/2).");

        var brokerIds = await _db.Brokers.Where(b => b.IsActive).Select(b => b.Id).ToListAsync();
        if (brokerIds.Count == 0)
            return await CreateErrorResponse(req, "No active brokers to assign as the winning broker.");
        if (body.BrokerIds is { Count: > 0 } && !body.BrokerIds.Any(b => brokerIds.Contains(b)))
            return await CreateErrorResponse(req, "None of the selected brokers are active brokers.");

        if (!_simQueue.IsConfigured)
            return await CreateErrorResponse(req, "Background queue (AzureWebJobsStorage) is not configured — the paced simulator needs it.");

        // Remaining un-typed unsold lots = the work to do; total target (capped at MaxLots) is fixed in
        // the message so progress reads consistently across the worker's re-enqueued continuations.
        var alreadyTyped = await _db.TypistEntries.Where(e => e.AuctionId == body.AuctionId).Select(e => e.LotNumber).Distinct().CountAsync();
        var remainingCount = await _db.Lots.CountAsync(l => l.AuctionId == body.AuctionId && l.Status != LotStatus.Sold
            && !_db.TypistEntries.Any(e => e.AuctionId == body.AuctionId && e.LotNumber == l.LotNumber));
        if (remainingCount == 0)
            return await CreateJsonResponse(req, new { auctionId = body.AuctionId, started = false, message = "No unsold un-typed lots to type in this auction." });

        var totalTarget = alreadyTyped + remainingCount;
        if (body.MaxLots is int mx && mx > 0) totalTarget = Math.Min(totalTarget, alreadyTyped + mx);
        var delay = Math.Clamp(body.DelaySeconds ?? 1, 0, 30);

        var msg = new TypistSimMessage(body.AuctionId, body.DisagreementPercent, body.MinPrice, body.MaxPrice,
            body.DisagreementType, body.BrokerIds, body.TypistUserId1, body.TypistUserId2, delay, totalTarget);
        auction.TypistSimStatus = $"Queued — target {totalTarget} lots at {delay}s/entry";
        auction.TypistSimStopRequested = false;   // clear any stale stop request from a previous run
        await _db.SaveChangesAsync();
        await _simQueue.EnqueueAsync(JsonSerializer.Serialize(msg, JsonOptions));

        return await CreateJsonResponse(req, new
        {
            auctionId = body.AuctionId,
            started = true,
            totalTarget,
            delaySeconds = delay,
            message = $"Started — typing {totalTarget} lot(s) in the background at {delay}s between entries. Watch the status."
        });
    }

    // Stop a running paced typist simulation. Sets the stop flag (raw UPDATE so it lands even while the
    // worker is mid-batch overwriting status); the worker checks it each lot, halts without re-enqueuing,
    // and clears it. Already-typed lots stay; a later Start resumes from the remaining un-typed lots.
    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("StopTypistSim")]
    public async Task<HttpResponseData> StopTypistSim(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "typist-entries/simulate/stop")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<StopSimRequest>();
        if (body == null || body.AuctionId <= 0)
            return await CreateErrorResponse(req, "auctionId is required");

        var n = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE auction.Auctions SET TypistSimStopRequested = 1 WHERE Id = {body.AuctionId}");
        if (n == 0) return await CreateErrorResponse(req, "Auction not found.");

        return await CreateJsonResponse(req, new { auctionId = body.AuctionId, stopping = true,
            message = "Stop requested — the typist simulator will halt within a lot or two." });
    }

    private record StopSimRequest(int AuctionId);

    // Background worker: types a time-budgeted batch of the auction's remaining un-typed lots at the
    // configured delay, then re-enqueues a continuation until the whole auction is typed. Each
    // invocation stays well under the queue's visibility timeout so the message isn't redelivered.
    [Function("TypistSimWorker")]
    public async Task TypistSimWorker(
        [QueueTrigger(Services.TypistSimQueue.QueueName, Connection = "AzureWebJobsStorage")] string message)
    {
        var msg = JsonSerializer.Deserialize<TypistSimMessage>(message, JsonOptions);
        if (msg == null) { _logger.LogWarning("Bad typist-sim message: {Msg}", message); return; }

        // NOTE: double-typing is prevented by the filtered UNIQUE index on TypistEntries plus the
        // detach-on-conflict in TypeOneLotAsync — if a redelivered/concurrent worker races on the same
        // lot, the second slot insert simply fails and that lot is skipped (no duplicate result). We do
        // NOT take a per-auction app-lock here: holding it for the whole batch and skipping a contended
        // continuation could delete that continuation message and stall the chain (observed as a hang).

        var auction = await _db.Auctions.FindAsync(msg.AuctionId);
        if (auction == null) { _logger.LogWarning("Typist-sim: auction {Id} not found", msg.AuctionId); return; }

        var pair = await ResolveSimTypistsAsync(msg.TypistUserId1, msg.TypistUserId2);
        if (pair == null) { auction.TypistSimStatus = "Failed: need two active Typist users"; await _db.SaveChangesAsync(); return; }
        var (a, b) = pair.Value;

        var activeBrokers = await _db.Brokers.Where(x => x.IsActive).Select(x => x.Id).ToListAsync();
        var winningPool = msg.BrokerIds is { Count: > 0 } ? msg.BrokerIds.Where(activeBrokers.Contains).Distinct().ToList() : activeBrokers;
        if (winningPool.Count == 0) { auction.TypistSimStatus = "Failed: no active brokers"; await _db.SaveChangesAsync(); return; }

        // Only win lots for brokers that ALREADY have a linked buyer, so every typed lot is sellable
        // through an existing broker→buyer combination (we use the links that are there; we never create
        // any). This is what makes the whole auction sellable end-to-end without inventing relationships.
        var linkedBrokers = (await _db.BrokerBuyers.Select(bb => bb.BrokerId).Distinct().ToListAsync()).ToHashSet();
        winningPool = winningPool.Where(linkedBrokers.Contains).ToList();
        if (winningPool.Count == 0)
        {
            auction.TypistSimStatus = "Failed: no active broker has a linked buyer — link customers to brokers first.";
            await _db.SaveChangesAsync();
            return;
        }

        var rnd = new Random();
        var minP = Math.Max(0.01m, msg.MinPrice ?? 50m);
        var maxP = Math.Max(minP, msg.MaxPrice ?? 500m);
        var disagreePct = Math.Clamp(msg.DisagreementPercent ?? 0, 0, 100);
        var disType = (msg.DisagreementType ?? "mixed").ToLowerInvariant();
        var delayMs = Math.Clamp(msg.DelaySeconds, 0, 30) * 1000;

        var typedCount = await _db.TypistEntries.Where(e => e.AuctionId == msg.AuctionId).Select(e => e.LotNumber).Distinct().CountAsync();
        if (typedCount >= msg.TotalTarget)
        {
            await FinishSimAsync(auction, msg.AuctionId, typedCount);
            return;
        }

        // Next batch of un-typed unsold lots (NOT EXISTS so it stays server-side even for a big auction).
        var remaining = await _db.Lots
            .Where(l => l.AuctionId == msg.AuctionId && l.Status != LotStatus.Sold
                && !_db.TypistEntries.Any(e => e.AuctionId == msg.AuctionId && e.LotNumber == l.LotNumber))
            .OrderBy(l => l.LotNumber)
            .Select(l => l.LotNumber)
            .Take(Math.Max(1, msg.TotalTarget - typedCount))
            .ToListAsync();

        // Stop requested between batches? Halt before doing any work.
        if (await StopRequestedAsync(msg.AuctionId)) { await StopSimAsync(auction, msg.AuctionId); return; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var budget = TimeSpan.FromMinutes(2); // comfortably under the 5-minute queue visibility timeout
        var stopped = false;
        foreach (var lot in remaining)
        {
            // Stop button pressed mid-batch — halt now and don't re-enqueue.
            if (await StopRequestedAsync(msg.AuctionId)) { stopped = true; break; }

            // Idempotency: a concurrent or redelivered batch may have typed this lot since `remaining`
            // was read — skip it so it can't be double-typed (extra entries / duplicate results).
            if (await _db.TypistEntries.AnyAsync(e => e.AuctionId == msg.AuctionId && e.LotNumber == lot))
                continue;
            try { await TypeOneLotAsync(msg.AuctionId, lot, a, b, winningPool, minP, maxP, disagreePct, disType, rnd, delayMs); typedCount++; }
            catch (Exception ex) { _logger.LogError(ex, "Typist-sim failed for lot {Lot}", lot); }
            auction.TypistSimStatus = $"Typing {typedCount}/{msg.TotalTarget}…";
            await _db.SaveChangesAsync();
            if (sw.Elapsed > budget) break;
        }

        if (stopped) { await StopSimAsync(auction, msg.AuctionId); return; }

        // Authoritative progress for the continue/finish decision: re-count distinct typed lots (handles
        // skips/concurrency) and only continue if this batch actually had work, so the target exceeding
        // the available lots can't make it re-enqueue forever.
        var actualTyped = await _db.TypistEntries.Where(e => e.AuctionId == msg.AuctionId).Select(e => e.LotNumber).Distinct().CountAsync();
        if (actualTyped < msg.TotalTarget && remaining.Count > 0)
            await _simQueue.EnqueueAsync(message);
        else
            await FinishSimAsync(auction, msg.AuctionId, actualTyped);
    }

    // Fresh (untracked) read of the stop flag — the worker holds a tracked `auction` whose copy is stale,
    // so we read the DB value directly each check.
    private async Task<bool> StopRequestedAsync(int auctionId)
        => await _db.Auctions.AsNoTracking().Where(a => a.Id == auctionId).Select(a => a.TypistSimStopRequested).FirstOrDefaultAsync();

    private async Task StopSimAsync(Auction auction, int auctionId)
    {
        var typed = await _db.TypistEntries.Where(e => e.AuctionId == auctionId).Select(e => e.LotNumber).Distinct().CountAsync();
        auction.TypistSimStatus = $"Stopped: typed {typed} lot(s) on request";
        await _db.SaveChangesAsync();
        // Clear the flag in the DB (the tracked entity's copy is stale, so a normal SaveChanges wouldn't write it).
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE auction.Auctions SET TypistSimStopRequested = 0 WHERE Id = {auctionId}");
    }

    private async Task FinishSimAsync(Auction auction, int auctionId, int typedCount)
    {
        var matched = await _db.TypistEntries.Where(e => e.AuctionId == auctionId && e.IsMatched).Select(e => e.LotNumber).Distinct().CountAsync();
        var disagreements = await _db.TypistEntries.Where(e => e.AuctionId == auctionId && e.IsDisagreement && !e.IsResolved).Select(e => e.LotNumber).Distinct().CountAsync();
        auction.TypistSimStatus = $"Done: typed {typedCount} lot(s) — {matched} matched, {disagreements} disagreement(s)";
        await _db.SaveChangesAsync();
    }

    // Types one lot as both typists (with the configured delay between entries) and runs the real match.
    private async Task TypeOneLotAsync(int auctionId, int lotNumber, AppUser a, AppUser b, List<int> pool,
        decimal minP, decimal maxP, int disagreePct, string disType, Random rnd, int delayMs)
    {
        decimal RandomPrice() => Math.Round(minP + (decimal)rnd.NextDouble() * (maxP - minP), 0, MidpointRounding.AwayFromZero);
        var brokerId = pool[rnd.Next(pool.Count)];
        var price = RandomPrice();
        var broker2 = brokerId;
        var price2 = price;
        if (rnd.Next(100) < disagreePct)
        {
            var kind = disType switch { "broker" => "broker", "price" => "price", _ => rnd.Next(2) == 0 ? "price" : "broker" };
            if (kind == "broker" && pool.Count > 1)
                broker2 = pool.Where(x => x != brokerId).ElementAt(rnd.Next(pool.Count - 1));
            else { price2 = RandomPrice(); if (price2 == price) price2 += 1m; }
        }

        var e1 = new TypistEntry { LotNumber = lotNumber, AuctionId = auctionId, BrokerId = brokerId, PriceEur = price, TypistUserId = a.Id, TypistSlot = 1, EnteredAt = DateTime.UtcNow };
        _db.TypistEntries.Add(e1);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException) { _db.Entry(e1).State = EntityState.Detached; throw; } // unique-index backstop: another worker already typed this lot/slot — skip it
        if (delayMs > 0) await Task.Delay(delayMs);

        var e2 = new TypistEntry { LotNumber = lotNumber, AuctionId = auctionId, BrokerId = broker2, PriceEur = price2, TypistUserId = b.Id, TypistSlot = 2, EnteredAt = DateTime.UtcNow };
        _db.TypistEntries.Add(e2);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException) { _db.Entry(e2).State = EntityState.Detached; throw; }

        await CompareEntries(e2, e1);
        if (delayMs > 0) await Task.Delay(delayMs);
    }

    // TEST TOOL (admin-only): clear an auction's disagreement queue by re-entering matching values as
    // both typists (the real reentry path: mark the disagreement rows resolved, then CompareEntries →
    // matched + AuctionResult). Resolves to the slot-1 entry's broker/price.
    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("ResolveTypistDisagreements")]
    public async Task<HttpResponseData> ResolveDisagreements(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "typist-entries/resolve-disagreements")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        int.TryParse(query["auctionId"], out var auctionId);
        if (auctionId <= 0) return await CreateErrorResponse(req, "auctionId is required");

        int.TryParse(query["typistUserId1"], out var qt1);
        int.TryParse(query["typistUserId2"], out var qt2);
        var pair = await ResolveSimTypistsAsync(qt1 > 0 ? qt1 : (int?)null, qt2 > 0 ? qt2 : (int?)null);
        if (pair == null) return await CreateErrorResponse(req, "Need two Typist users.");
        var (typistA, typistB) = pair.Value;

        var disEntries = await _db.TypistEntries
            .Where(e => e.AuctionId == auctionId && e.IsDisagreement && !e.IsResolved)
            .ToListAsync();
        var lots = disEntries.GroupBy(e => e.LotNumber).ToList();
        if (lots.Count == 0)
            return await CreateJsonResponse(req, new { auctionId, resolved = 0, errors = 0, message = "No pending disagreements." });

        int resolved = 0, errors = 0;
        foreach (var grp in lots)
        {
            try
            {
                var lotNumber = grp.Key;
                var src = grp.OrderBy(e => e.TypistSlot).First();
                var brokerId = src.BrokerId;
                var price = src.PriceEur;

                foreach (var old in grp) old.IsResolved = true;
                await _db.SaveChangesAsync();

                var r1 = new TypistEntry { LotNumber = lotNumber, AuctionId = auctionId, BrokerId = brokerId, PriceEur = price, TypistUserId = typistA.Id, TypistSlot = 1, EnteredAt = DateTime.UtcNow };
                _db.TypistEntries.Add(r1);
                await _db.SaveChangesAsync();

                var r2 = new TypistEntry { LotNumber = lotNumber, AuctionId = auctionId, BrokerId = brokerId, PriceEur = price, TypistUserId = typistB.Id, TypistSlot = 2, EnteredAt = DateTime.UtcNow };
                _db.TypistEntries.Add(r2);
                await _db.SaveChangesAsync();

                await CompareEntries(r2, r1);
                if (r2.IsMatched) resolved++;
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogError(ex, "Resolve disagreement failed for lot {Lot}", grp.Key);
            }
        }

        return await CreateJsonResponse(req, new { auctionId, resolved, errors });
    }

    private async Task CompareEntries(TypistEntry entry1, TypistEntry entry2)
    {
        if (entry1.BrokerId == entry2.BrokerId && entry1.PriceEur == entry2.PriceEur)
        {
            // Match — create the auction result via the shared sale path (identical to the external feed).
            entry1.IsMatched = true;
            entry2.IsMatched = true;
            entry1.MatchedWithEntryId = entry2.Id;
            entry2.MatchedWithEntryId = entry1.Id;

            var result = await RecordSaleAsync(entry1.AuctionId, entry1.LotNumber, entry1.BrokerId, entry1.PriceEur);
            if (result != null)
            {
                entry1.AuctionResultId = result.Id;
                entry2.AuctionResultId = result.Id;
            }
            await _db.SaveChangesAsync();

            _logger.LogInformation("Typist entries matched for lot {LotNumber}: broker={BrokerId}, price={Price}",
                entry1.LotNumber, entry1.BrokerId, entry1.PriceEur);
        }
        else
        {
            // Disagreement
            entry1.IsDisagreement = true;
            entry2.IsDisagreement = true;
            entry1.MatchedWithEntryId = entry2.Id;
            entry2.MatchedWithEntryId = entry1.Id;

            await _db.SaveChangesAsync();

            _logger.LogWarning("Typist disagreement for lot {LotNumber}: entry1(broker={B1}, price={P1}) vs entry2(broker={B2}, price={P2})",
                entry1.LotNumber, entry1.BrokerId, entry1.PriceEur, entry2.BrokerId, entry2.PriceEur);
        }
    }

    // Shared sale creation used by BOTH the typist match (CompareEntries) and the external price feed
    // (SubmitExternalResults): create the AuctionResult (from CatalogLot data, else Lot data), mark the lot
    // Sold + HammerPrice, and write the journal transactions — so a sale is identical downstream (invoicing,
    // BC push) no matter the source. Idempotent: returns null without changes if the lot is already Sold
    // (never overwrites a price), which is also what lets external + typist coexist lot-by-lot — first
    // authoritative record wins, the other is blocked.
    private async Task<AuctionResult?> RecordSaleAsync(int auctionId, int lotNumber, int brokerId, decimal priceEur, string? externalRef = null)
    {
        var auctionLot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == lotNumber && l.AuctionId == auctionId);
        if (auctionLot != null && auctionLot.Status == LotStatus.Sold)
            return null;

        var catalogLot = await _catalogDb.CatalogLots.FirstOrDefaultAsync(cl => cl.LotNumber == lotNumber);

        var result = new AuctionResult
        {
            AuctionId = auctionId,
            LotNumber = lotNumber,
            BrokerId = brokerId,
            PriceEur = priceEur,
            SalesType = catalogLot?.SalesType ?? auctionLot?.Description?.Split(' ').FirstOrDefault(),
            Gender = catalogLot?.Gender ?? (auctionLot != null ? ParseField(auctionLot.Description, 1) : null),
            Group = catalogLot?.Group ?? auctionLot?.Category,
            Color = catalogLot?.Color ?? (auctionLot != null ? ParseField(auctionLot.Description, 2) : null),
            Quality = catalogLot?.Quality ?? (auctionLot != null ? ParseField(auctionLot.Description, 3) : null),
            Size = catalogLot?.Size,
            HairLength = catalogLot?.HairLength,
            Clarity = catalogLot?.Clarity,
            TotalSkins = catalogLot?.TotalSkins ?? auctionLot?.Quantity ?? 0,
            BoxCount = catalogLot?.BoxCount ?? 0,
            ExternalRef = externalRef,
            Processed = false,
            ReceivedAt = DateTime.UtcNow
        };

        _db.AuctionResults.Add(result);
        await _db.SaveChangesAsync();

        if (auctionLot != null)
        {
            auctionLot.Status = LotStatus.Sold;
            auctionLot.HammerPrice = priceEur;
            result.Processed = true;
            result.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        await CreateTransactionsForMatch(result, auctionLot);
        return result;
    }

    private async Task CreateTransactionsForMatch(AuctionResult result, Lot? auctionLot)
    {
        if (auctionLot == null) return;

        var auctionId = auctionLot.AuctionId;
        var quantity = result.TotalSkins;
        var pricePerSkin = result.PriceEur;
        var hammerPrice = quantity * pricePerSkin;
        var lotDesc = $"Lot {result.LotNumber} — {auctionLot.Description}";

        // Read fee/commission parameters
        decimal auctionFeePercent = 0;
        decimal handlingFeePerSkin = 0;

        var auctionFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "AuctionFee");
        var handlingFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "HandlingFee");
        if (auctionFeeParam != null)
            decimal.TryParse(auctionFeeParam.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out auctionFeePercent);
        if (handlingFeeParam != null)
            decimal.TryParse(handlingFeeParam.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out handlingFeePerSkin);

        // 1. Lot Sale transaction
        _db.AuctionTransactions.Add(new AuctionTransaction
        {
            AuctionId = auctionId,
            LotNumber = result.LotNumber,
            TransactionType = TransactionType.LotSale,
            BrokerId = result.BrokerId,
            Description = lotDesc,
            Quantity = quantity,
            UnitPrice = pricePerSkin,
            Amount = hammerPrice,
            AuctionResultId = result.Id,
            CreatedAt = DateTime.UtcNow
        });

        // 2. Auction Fee transaction
        var handlingTotal = quantity * handlingFeePerSkin;
        var auctionFeeAmount = (hammerPrice + handlingTotal) * auctionFeePercent / 100m;
        if (auctionFeeAmount > 0)
        {
            _db.AuctionTransactions.Add(new AuctionTransaction
            {
                AuctionId = auctionId,
                LotNumber = result.LotNumber,
                TransactionType = TransactionType.AuctionFee,
                BrokerId = result.BrokerId,
                Description = $"Auction Fee — {lotDesc} ({auctionFeePercent}%)",
                Quantity = quantity,
                UnitPrice = Math.Round(auctionFeeAmount / quantity, 4),
                Amount = Math.Round(auctionFeeAmount, 2),
                AuctionResultId = result.Id,
                CreatedAt = DateTime.UtcNow
            });
        }

        // 3. Commission transaction (if set on the result)
        var commissionAmount = result.CommissionAmount ?? 0;
        if (commissionAmount > 0)
        {
            _db.AuctionTransactions.Add(new AuctionTransaction
            {
                AuctionId = auctionId,
                LotNumber = result.LotNumber,
                TransactionType = TransactionType.Commission,
                BrokerId = result.BrokerId,
                Description = $"Commission — {lotDesc} ({result.CommissionType} {result.CommissionValue})",
                Quantity = quantity,
                UnitPrice = Math.Round(commissionAmount / quantity, 4),
                Amount = Math.Round(commissionAmount, 2),
                AuctionResultId = result.Id,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Created auction transactions for lot {LotNumber}: sale={Sale}, fee={Fee}, commission={Commission}",
            result.LotNumber, hammerPrice, auctionFeeAmount, commissionAmount);
    }

    private static string? ParseField(string? description, int index)
    {
        if (string.IsNullOrEmpty(description)) return null;
        var parts = description.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return index < parts.Length ? parts[index] : null;
    }

    [Function("GetTypistEntries")]
    public async Task<HttpResponseData> GetEntries(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "typist-entries")] HttpRequestData req)
    {
        var entries = await _db.TypistEntries
            .Include(e => e.Broker)
            .Include(e => e.TypistUser)
            .OrderByDescending(e => e.EnteredAt)
            .Take(100)
            .Select(e => new
            {
                e.Id, e.LotNumber, e.BrokerId,
                brokerNumber = e.Broker.BrokerNumber,
                brokerName = e.Broker.CompanyName,
                e.PriceEur, e.TypistUserId,
                typistName = e.TypistUser.DisplayName,
                e.TypistSlot, e.EnteredAt,
                e.IsMatched, e.IsDisagreement, e.IsResolved,
                e.MatchedWithEntryId, e.AuctionResultId
            })
            .ToListAsync();

        return await CreateJsonResponse(req, entries);
    }

    [Function("GetDisagreements")]
    public async Task<HttpResponseData> GetDisagreements(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "typist-entries/disagreements")] HttpRequestData req)
    {
        // Get all disagreements that haven't been resolved
        var disagreements = await _db.TypistEntries
            .Include(e => e.Broker)
            .Include(e => e.TypistUser)
            .Where(e => e.IsDisagreement && !e.IsResolved)
            .OrderByDescending(e => e.EnteredAt)
            .Select(e => new
            {
                e.Id, e.LotNumber, e.BrokerId,
                brokerNumber = e.Broker.BrokerNumber,
                brokerName = e.Broker.CompanyName,
                e.PriceEur, e.TypistUserId,
                typistName = e.TypistUser.DisplayName,
                e.TypistSlot, e.EnteredAt,
                e.MatchedWithEntryId
            })
            .ToListAsync();

        // Group by lot number for easier display
        var grouped = disagreements
            .GroupBy(d => d.LotNumber)
            .Select(g => new
            {
                lotNumber = g.Key,
                entries = g.ToList()
            })
            .ToList();

        return await CreateJsonResponse(req, grouped);
    }

    [Function("GetTypistStatus")]
    public async Task<HttpResponseData> GetStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "typist-entries/status/{lotNumber:int}")] HttpRequestData req,
        int lotNumber)
    {
        var entries = await _db.TypistEntries
            .Include(e => e.Broker)
            .Include(e => e.TypistUser)
            .Where(e => e.LotNumber == lotNumber && !e.IsResolved)
            .OrderBy(e => e.TypistSlot)
            .Select(e => new
            {
                e.Id, e.LotNumber, e.BrokerId,
                brokerNumber = e.Broker.BrokerNumber,
                brokerName = e.Broker.CompanyName,
                e.PriceEur, e.TypistUserId,
                typistName = e.TypistUser.DisplayName,
                e.TypistSlot, e.EnteredAt,
                e.IsMatched, e.IsDisagreement
            })
            .ToListAsync();

        return await CreateJsonResponse(req, new
        {
            lotNumber,
            entriesCount = entries.Count,
            isComplete = entries.Any(e => e.IsMatched || e.IsDisagreement),
            isMatched = entries.Any(e => e.IsMatched),
            isDisagreement = entries.Any(e => e.IsDisagreement),
            entries
        });
    }

    // Option A: a lot's DISPLAY text/skins come from the frozen snapshot auction.[{Num}.Lots] (the
    // authoritative catalogue), not the materialised Lot.Description. Returns null -> caller falls back to
    // the Lot row (no auction scope, or the snapshot isn't built yet).
    private async Task<(string Description, string? Category, int Quantity)?> SnapshotLotDisplayAsync(int auctionId, int lotNumber)
    {
        if (auctionId <= 0) return null;
        var auctionNum = await _db.Auctions.Where(a => a.Id == auctionId).Select(a => a.AuctionNumber).FirstOrDefaultAsync();
        if (string.IsNullOrEmpty(auctionNum)) return null;

        await using var conn = new SqlConnection(_catalogDb.Database.GetConnectionString());
        await conn.OpenAsync();

        await using (var check = new SqlCommand("SELECT OBJECT_ID(@t, 'U')", conn))
        {
            check.Parameters.AddWithValue("@t", $"auction.[{auctionNum}.Lots]");
            if (await check.ExecuteScalarAsync() is null or System.DBNull) return null;   // not imported yet
        }

        await using var cmd = new SqlCommand(
            $@"SELECT TOP 1 SalesType, Gender, [Group], HairLength, Size, Quality, Color, Clarity, Damages, TotalSkins
               FROM auction.[{auctionNum}.Lots] WHERE LotNumber = @ln", conn);
        cmd.Parameters.AddWithValue("@ln", lotNumber);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim();
        var group = S(2);
        var damages = S(8);
        var parts = new[] { S(0), S(1), group, S(3), S(4), S(5), S(6), S(7),
            (damages != null && !damages.Equals("None", System.StringComparison.OrdinalIgnoreCase)) ? damages : null }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var description = string.Join(" ", parts);
        var totalSkins = r.IsDBNull(9) ? 0 : r.GetInt32(9);
        return (description, group, totalSkins);
    }

    [Function("GetNextUnsoldLotForTypist")]
    public async Task<HttpResponseData> GetNextUnsoldLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "typist-entries/next-unsold-lot")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var typistUserIdStr = query["typistUserId"];
        var auctionIdStr = query["auctionId"];
        int.TryParse(auctionIdStr, out var auctionId);

        // Lots already matched (sold) — skip these for everyone
        var matchedQuery = _db.TypistEntries.Where(e => e.IsMatched);
        if (auctionId > 0) matchedQuery = matchedQuery.Where(e => e.AuctionId == auctionId);
        var matchedLotNumbers = await matchedQuery.Select(e => e.LotNumber).Distinct().ToListAsync();

        // Lots this typist already entered (and entry is still active — not resolved)
        var myEnteredLotNumbers = new List<int>();
        if (int.TryParse(typistUserIdStr, out var typistUserId))
        {
            var myQuery = _db.TypistEntries.Where(e => e.TypistUserId == typistUserId && !e.IsResolved);
            if (auctionId > 0) myQuery = myQuery.Where(e => e.AuctionId == auctionId);
            myEnteredLotNumbers = await myQuery.Select(e => e.LotNumber).Distinct().ToListAsync();
        }

        var skipLots = matchedLotNumbers.Union(myEnteredLotNumbers).ToList();

        var lotsQuery = _db.Lots.Where(l => l.Status != LotStatus.Sold && !skipLots.Contains(l.LotNumber));
        if (auctionId > 0) lotsQuery = lotsQuery.Where(l => l.AuctionId == auctionId);

        var nextLot = await lotsQuery
            .OrderBy(l => l.LotNumber)
            .Select(l => new { l.LotNumber, l.Description, l.Category, l.Quantity, l.Unit })
            .FirstOrDefaultAsync();

        if (nextLot == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var snap = await SnapshotLotDisplayAsync(auctionId, nextLot.LotNumber);
        return await CreateJsonResponse(req, new
        {
            nextLot.LotNumber,
            Description = snap?.Description ?? nextLot.Description,
            Category = snap?.Category ?? nextLot.Category,
            Quantity = snap?.Quantity ?? nextLot.Quantity,
            nextLot.Unit
        });
    }

    [Function("GetTypistLot")]
    public async Task<HttpResponseData> GetTypistLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "typist-entries/lot/{lotNumber:int}")] HttpRequestData req, int lotNumber)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        int.TryParse(query["auctionId"], out var auctionId);
        int.TryParse(query["typistUserId"], out var typistUserId);

        // Lot must exist in this auction
        var lotQuery = _db.Lots.Where(l => l.LotNumber == lotNumber);
        if (auctionId > 0) lotQuery = lotQuery.Where(l => l.AuctionId == auctionId);
        var lot = await lotQuery.OrderBy(l => l.AuctionId).FirstOrDefaultAsync();
        if (lot == null)
            return await CreateJsonResponse(req, new { error = $"Lot {lotNumber} isn't in this auction." }, System.Net.HttpStatusCode.NotFound);

        // Already sold
        if (lot.Status == LotStatus.Sold)
            return await CreateJsonResponse(req, new { error = $"Lot {lotNumber} is already sold." }, System.Net.HttpStatusCode.Conflict);

        // Already matched (recorded) — same skip rule as the auto-feed
        var matchedQuery = _db.TypistEntries.Where(e => e.LotNumber == lotNumber && e.IsMatched);
        if (auctionId > 0) matchedQuery = matchedQuery.Where(e => e.AuctionId == auctionId);
        if (await matchedQuery.AnyAsync())
            return await CreateJsonResponse(req, new { error = $"Lot {lotNumber} is already recorded." }, System.Net.HttpStatusCode.Conflict);

        // Already entered (still active) by THIS typist — the other typist's pending entry is fine (double-entry)
        if (typistUserId > 0)
        {
            var mineQuery = _db.TypistEntries.Where(e => e.LotNumber == lotNumber && e.TypistUserId == typistUserId && !e.IsResolved);
            if (auctionId > 0) mineQuery = mineQuery.Where(e => e.AuctionId == auctionId);
            if (await mineQuery.AnyAsync())
                return await CreateJsonResponse(req, new { error = $"You have already entered Lot {lotNumber}." }, System.Net.HttpStatusCode.Conflict);
        }

        var snap = await SnapshotLotDisplayAsync(auctionId, lotNumber);
        return await CreateJsonResponse(req, new
        {
            lot.LotNumber,
            Description = snap?.Description ?? lot.Description,
            Category = snap?.Category ?? lot.Category,
            Quantity = snap?.Quantity ?? lot.Quantity,
            lot.Unit
        });
    }

    [Function("GetRecentMatchedEntries")]
    public async Task<HttpResponseData> GetRecentMatched(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "typist-entries/recent")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        int.TryParse(query["auctionId"], out var auctionId);

        var entriesQuery = _db.TypistEntries
            .Include(e => e.Broker)
            .Include(e => e.TypistUser)
            .Where(e => e.TypistSlot == 1);

        if (auctionId > 0)
            entriesQuery = entriesQuery.Where(e => e.AuctionId == auctionId);

        var recentLots = await entriesQuery
            .OrderByDescending(e => e.EnteredAt)
            .Take(50)
            .Select(e => new
            {
                e.Id, e.LotNumber, e.BrokerId,
                brokerNumber = e.Broker.BrokerNumber,
                brokerName = e.Broker.CompanyName,
                e.PriceEur, e.TypistSlot, e.EnteredAt,
                e.IsMatched, e.IsDisagreement, e.IsResolved,
                e.AuctionResultId
            })
            .ToListAsync();

        return await CreateJsonResponse(req, recentLots);
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("ResetTypistLot")]
    public async Task<HttpResponseData> ResetLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "typist-entries/reset-lot/{lotNumber:int}")] HttpRequestData req,
        int lotNumber)
    {
        try
        {
            var entries = await _db.TypistEntries
                .Where(e => e.LotNumber == lotNumber)
                .ToListAsync();

            if (entries.Count == 0)
                return await CreateErrorResponse(req, $"No entries found for lot {lotNumber}");

            // Clear circular FK references (MatchedWithEntryId) before deleting
            foreach (var e in entries)
                e.MatchedWithEntryId = null;
            await _db.SaveChangesAsync();

            // Remove auction result and transactions first (FK constraints)
            var resultIds = entries.Where(e => e.AuctionResultId.HasValue).Select(e => e.AuctionResultId!.Value).Distinct().ToList();
            if (resultIds.Count > 0)
            {
                var transactions = await _db.AuctionTransactions.Where(t => resultIds.Contains(t.AuctionResultId ?? 0)).ToListAsync();
                _db.AuctionTransactions.RemoveRange(transactions);

                // Clear FK on entries before deleting results
                foreach (var e in entries)
                    e.AuctionResultId = null;
                await _db.SaveChangesAsync();

                var results = await _db.AuctionResults.Where(r => resultIds.Contains(r.Id)).ToListAsync();
                _db.AuctionResults.RemoveRange(results);
                await _db.SaveChangesAsync();
            }

            _db.TypistEntries.RemoveRange(entries);

            // Also reset the lot status back to unsold
            var lot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == lotNumber);
            if (lot != null && lot.Status == LotStatus.Sold)
            {
                lot.Status = LotStatus.Active;
                lot.HammerPrice = null;
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation("Reset lot {LotNumber}: removed {Count} entries", lotNumber, entries.Count);

            return await CreateJsonResponse(req, new { success = true, lotNumber, entriesRemoved = entries.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset lot {LotNumber}", lotNumber);
            return await CreateErrorResponse(req, $"Failed to reset lot: {ex.Message}");
        }
    }

    [Function("GetActiveTypists")]
    public async Task<HttpResponseData> GetActiveTypists(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "typist-entries/active-typists")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        int.TryParse(query["auctionId"], out var auctionId);

        if (auctionId <= 0)
            return await CreateErrorResponse(req, "auctionId is required");

        // Active typists = distinct users who have submitted entries for this auction that are not fully resolved
        var activeTypistIds = await _db.TypistEntries
            .Where(e => e.AuctionId == auctionId && !e.IsResolved && !e.IsMatched)
            .Select(e => e.TypistUserId)
            .Distinct()
            .ToListAsync();

        return await CreateJsonResponse(req, new { auctionId, activeTypistIds, count = activeTypistIds.Count });
    }

    // External price feed: an authoritative outside system (the auction clerk/clock) POSTs realtime
    // knock-downs — one or many — and we create the sale directly via RecordSaleAsync, skipping the
    // two-typist verification. Machine auth via x-api-key against EXTERNAL_PRICE_API_KEY (NOT a user role);
    // the route must be in Easy Auth excludedPaths. Idempotent: an already-Sold lot is skipped, so
    // redelivery is safe and external + typist coexist on the same auction lot-by-lot.
    [AllowAnonymous]
    [Function("SubmitExternalAuctionResults")]
    public async Task<HttpResponseData> SubmitExternalResults(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results/external")] HttpRequestData req)
    {
        if (!CheckExternalApiKey(req))
        {
            var unauth = req.CreateResponse(System.Net.HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync("Invalid or missing x-api-key.");
            return unauth;
        }

        var body = await req.ReadFromJsonAsync<ExternalResultsRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.AuctionNumber) || body.Results == null || body.Results.Count == 0)
            return await CreateErrorResponse(req, "auctionNumber and at least one result are required.");

        var auction = await _db.Auctions.FirstOrDefaultAsync(a => a.AuctionNumber == body.AuctionNumber);
        if (auction == null)
            return await CreateErrorResponse(req, $"Auction '{body.AuctionNumber}' not found.");

        int recorded = 0, skipped = 0, errors = 0;
        var outcomes = new List<object>();
        foreach (var r in body.Results)
        {
            var broker = await _db.Brokers.FirstOrDefaultAsync(b => b.BrokerNumber == r.BrokerNumber);
            if (broker == null)
            {
                errors++;
                outcomes.Add(new { r.LotNumber, status = "broker-not-found", r.BrokerNumber });
                continue;
            }

            var lot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == r.LotNumber && l.AuctionId == auction.Id);
            if (lot == null)
            {
                errors++;
                outcomes.Add(new { r.LotNumber, status = "lot-not-found" });
                continue;
            }
            if (lot.Status == LotStatus.Sold)
            {
                skipped++;
                outcomes.Add(new { r.LotNumber, status = "skipped-already-sold" });
                continue;
            }

            var result = await RecordSaleAsync(auction.Id, r.LotNumber, broker.Id, r.PriceEur, r.ExternalRef);
            if (result == null)
            {
                skipped++;
                outcomes.Add(new { r.LotNumber, status = "skipped-already-sold" });
                continue;
            }
            recorded++;
            outcomes.Add(new { r.LotNumber, status = "recorded", auctionId = auction.Id, resultId = result.Id });
        }

        _logger.LogInformation("External results for {Auction}: recorded={Recorded}, skipped={Skipped}, errors={Errors}",
            body.AuctionNumber, recorded, skipped, errors);

        return await CreateJsonResponse(req, new { recorded, skipped, errors, results = outcomes });
    }

    // Machine-to-machine auth for the external price feed. Fails closed: 401 if EXTERNAL_PRICE_API_KEY isn't
    // configured or the x-api-key header doesn't match (same pattern as LOT_GEN_API_KEY).
    private bool CheckExternalApiKey(HttpRequestData req)
    {
        var expected = _configuration["EXTERNAL_PRICE_API_KEY"] ?? _configuration["Values:EXTERNAL_PRICE_API_KEY"];
        if (string.IsNullOrEmpty(expected)) return false;
        var provided = req.Headers.TryGetValues("x-api-key", out var vals) ? vals.FirstOrDefault() : null;
        return !string.IsNullOrEmpty(provided) && string.Equals(provided, expected, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseData> CreateJsonResponse<T>(
        HttpRequestData req, T data, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(data, JsonOptions));
        return response;
    }

    private static async Task<HttpResponseData> CreateErrorResponse(HttpRequestData req, string error)
    {
        var response = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { error }, JsonOptions));
        return response;
    }
}

public record SubmitTypistEntryRequest(int LotNumber, int BrokerId, decimal PriceEur, int TypistUserId, int AuctionId = 0);

// External price feed payload: an auction number plus the realtime knock-downs (lot, winning broker number,
// hammer price in EUR, and the source's own reference). Identifiers are "our numbers" (BrokerNumber / LotNumber).
public record ExternalResultsRequest(string AuctionNumber, List<ExternalResultItem> Results);
public record ExternalResultItem(int LotNumber, string BrokerNumber, decimal PriceEur, string? ExternalRef = null);
