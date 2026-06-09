using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class TypistEntryFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly ILogger<TypistEntryFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TypistEntryFunctions(AuctionDbContext db, CatalogDbContext catalogDb, ILogger<TypistEntryFunctions> logger)
    {
        _db = db;
        _catalogDb = catalogDb;
        _logger = logger;
    }

    [Function("SubmitTypistEntry")]
    public async Task<HttpResponseData> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "typist-entries")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SubmitTypistEntryRequest>();
        if (body == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

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

        // If both entries exist, compare them
        if (slot == 2)
        {
            var otherEntry = existingEntries[0];
            await CompareEntries(entry, otherEntry);
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

    [Function("SubmitDisagreementReentry")]
    public async Task<HttpResponseData> SubmitReentry(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "typist-entries/reentry")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SubmitTypistEntryRequest>();
        if (body == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

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

        var entry = new TypistEntry
        {
            LotNumber = body.LotNumber,
            BrokerId = body.BrokerId,
            PriceEur = body.PriceEur,
            TypistUserId = body.TypistUserId,
            TypistSlot = slot,
            EnteredAt = DateTime.UtcNow
        };

        _db.TypistEntries.Add(entry);
        await _db.SaveChangesAsync();

        // Only compare and resolve when BOTH typists have re-entered
        if (slot == 2)
        {
            // Now mark old disagreement entries as resolved
            var oldEntries = await _db.TypistEntries
                .Where(e => e.LotNumber == body.LotNumber && e.IsDisagreement && !e.IsResolved)
                .ToListAsync();

            foreach (var old in oldEntries)
                old.IsResolved = true;

            await _db.SaveChangesAsync();

            var otherEntry = existingReentries[0];
            await CompareEntries(entry, otherEntry);
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

    private async Task CompareEntries(TypistEntry entry1, TypistEntry entry2)
    {
        if (entry1.BrokerId == entry2.BrokerId && entry1.PriceEur == entry2.PriceEur)
        {
            // Match — create auction result
            entry1.IsMatched = true;
            entry2.IsMatched = true;
            entry1.MatchedWithEntryId = entry2.Id;
            entry2.MatchedWithEntryId = entry1.Id;

            // Create the auction result from CatalogLot (preferred) or Lot data
            var auctionLot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == entry1.LotNumber);
            var catalogLot = await _catalogDb.CatalogLots.FirstOrDefaultAsync(cl => cl.LotNumber == entry1.LotNumber);

            var result = new AuctionResult
            {
                LotNumber = entry1.LotNumber,
                BrokerId = entry1.BrokerId,
                PriceEur = entry1.PriceEur,
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
                Processed = false,
                ReceivedAt = DateTime.UtcNow
            };

            _db.AuctionResults.Add(result);
            await _db.SaveChangesAsync();

            entry1.AuctionResultId = result.Id;
            entry2.AuctionResultId = result.Id;

            // Process the lot (mark as sold, assign to broker)
            if (auctionLot != null)
            {
                auctionLot.Status = LotStatus.Sold;
                auctionLot.HammerPrice = entry1.PriceEur;

                result.Processed = true;
                result.ProcessedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();

            // Create auction transactions (journal entries)
            await CreateTransactionsForMatch(result, auctionLot);

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

        return await CreateJsonResponse(req, nextLot);
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

    [Function("ResetTypistLot")]
    public async Task<HttpResponseData> ResetLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "typist-entries/reset-lot/{lotNumber:int}")] HttpRequestData req,
        int lotNumber)
    {
        var entries = await _db.TypistEntries
            .Where(e => e.LotNumber == lotNumber)
            .ToListAsync();

        if (entries.Count == 0)
            return await CreateErrorResponse(req, $"No entries found for lot {lotNumber}");

        _db.TypistEntries.RemoveRange(entries);

        // Also reset the lot status back to unsold if it was marked sold by these entries
        var lot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == lotNumber);
        if (lot != null && lot.Status == LotStatus.Sold)
        {
            lot.Status = LotStatus.Active;
            lot.HammerPrice = null;
        }

        // Remove auction result if created
        var resultIds = entries.Where(e => e.AuctionResultId.HasValue).Select(e => e.AuctionResultId!.Value).Distinct().ToList();
        if (resultIds.Count > 0)
        {
            var results = await _db.AuctionResults.Where(r => resultIds.Contains(r.Id)).ToListAsync();
            var transactions = await _db.AuctionTransactions.Where(t => resultIds.Contains(t.AuctionResultId ?? 0)).ToListAsync();
            _db.AuctionTransactions.RemoveRange(transactions);
            _db.AuctionResults.RemoveRange(results);
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Reset lot {LotNumber}: removed {Count} entries", lotNumber, entries.Count);

        return await CreateJsonResponse(req, new { success = true, lotNumber, entriesRemoved = entries.Count });
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
