using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Functions.Auth;
using AuctionSystem.Functions.BusinessCentral.Services;
using AuctionSystem.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class AuctionResultFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly ILogger<AuctionResultFunctions> _logger;
    private readonly BlobStorageService? _blobStorage;
    private readonly BusinessCentralSyncService? _bcSyncService;
    private readonly BcPushQueue? _bcPushQueue;
    private readonly IServiceScopeFactory? _scopeFactory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AuctionResultFunctions(AuctionDbContext db, CatalogDbContext catalogDb, ILogger<AuctionResultFunctions> logger, BlobStorageService? blobStorage = null, BusinessCentralSyncService? bcSyncService = null, BcPushQueue? bcPushQueue = null, IServiceScopeFactory? scopeFactory = null)
    {
        _db = db;
        _catalogDb = catalogDb;
        _logger = logger;
        _blobStorage = blobStorage;
        _bcSyncService = bcSyncService;
        _bcPushQueue = bcPushQueue;
        _scopeFactory = scopeFactory;
    }

    [Function("SubmitAuctionResult")]
    public async Task<HttpResponseData> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SubmitAuctionResultRequest>();
        if (body == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var broker = await _db.Brokers.FindAsync(body.BrokerId);
        if (broker == null)
        {
            var response = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { error = "Broker not found" }, JsonOptions));
            return response;
        }

        // Look up lot in auction.Lots and snapshot/CatalogLots
        var auctionLot = await _db.Lots
            .Include(l => l.Auction)
            .FirstOrDefaultAsync(l => l.LotNumber == body.LotNumber);

        // Try snapshot table first, fall back to live CatalogLots
        CatalogLot? catalogLot = null;
        if (auctionLot?.Auction?.AuctionNumber != null)
        {
            var snapTable = $"auction.[{auctionLot.Auction.AuctionNumber}.Lots]";
            try
            {
                catalogLot = await _catalogDb.Database
                    .SqlQueryRaw<CatalogLot>($"SELECT * FROM {snapTable} WHERE LotNumber = {body.LotNumber}")
                    .FirstOrDefaultAsync();
            }
            catch { }
        }
        catalogLot ??= await _catalogDb.CatalogLots
            .FirstOrDefaultAsync(cl => cl.LotNumber == body.LotNumber);

        var result = new AuctionResult
        {
            AuctionId = auctionLot?.AuctionId ?? 0,
            LotNumber = body.LotNumber,
            BrokerId = body.BrokerId,
            PriceEur = body.PriceEur,
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

        await ProcessAuctionResult(result, auctionLot);

        var resp = req.CreateResponse(System.Net.HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/json");
        await resp.WriteStringAsync(JsonSerializer.Serialize(new
        {
            resultId = result.Id,
            lotNumber = result.LotNumber,
            brokerId = result.BrokerId,
            priceEur = result.PriceEur,
            processed = result.Processed,
            lotFound = auctionLot != null,
            salesType = result.SalesType,
            gender = result.Gender,
            group = result.Group,
            color = result.Color,
            quality = result.Quality,
            totalSkins = result.TotalSkins
        }, JsonOptions));
        return resp;
    }

    private static string? ParseField(string? description, int index)
    {
        if (string.IsNullOrEmpty(description)) return null;
        var parts = description.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return index < parts.Length ? parts[index] : null;
    }

    private async Task ProcessAuctionResult(AuctionResult result, Lot? existingLot)
    {
        try
        {
            if (existingLot == null)
            {
                var activeAuction = await _db.Auctions
                    .Where(a => a.Status == AuctionStatus.Active)
                    .OrderByDescending(a => a.ScheduledDate)
                    .FirstOrDefaultAsync();

                if (activeAuction == null)
                {
                    activeAuction = await _db.Auctions
                        .OrderByDescending(a => a.Id)
                        .FirstOrDefaultAsync();
                }

                if (activeAuction == null)
                {
                    _logger.LogWarning("No auction found to assign lot {LotNumber}", result.LotNumber);
                    return;
                }

                existingLot = new Lot
                {
                    AuctionId = activeAuction.Id,
                    LotNumber = result.LotNumber,
                    Description = $"Lot {result.LotNumber}",
                    Quantity = 1,
                    Unit = "skins",
                    StartingPrice = 0,
                    HammerPrice = result.PriceEur,
                    Status = LotStatus.Broker
                };
                _db.Lots.Add(existingLot);
                await _db.SaveChangesAsync();
            }
            else
            {
                existingLot.HammerPrice = result.PriceEur;
                existingLot.Status = LotStatus.Broker;
                await _db.SaveChangesAsync();
            }

            var existingAllocation = await _db.LotAllocations
                .FirstOrDefaultAsync(a => a.LotId == existingLot.Id && a.BrokerId == result.BrokerId);

            if (existingAllocation == null)
            {
                var defaultBuyer = await _db.Buyers
                    .FirstOrDefaultAsync(b => b.BrokerId == result.BrokerId);

                if (defaultBuyer == null)
                {
                    defaultBuyer = await _db.Buyers.FirstOrDefaultAsync();
                }

                if (defaultBuyer == null)
                {
                    _logger.LogWarning("No buyer found to create allocation for lot {LotNumber}", result.LotNumber);
                    return;
                }

                var allocation = new LotAllocation
                {
                    LotId = existingLot.Id,
                    BrokerId = result.BrokerId,
                    BuyerId = defaultBuyer.Id,
                    Quantity = existingLot.Quantity,
                    PricePerUnit = existingLot.Quantity > 0 ? result.PriceEur / existingLot.Quantity : result.PriceEur,
                    TotalPrice = result.PriceEur,
                    Status = AllocationStatus.Allocated,
                    AllocatedAt = DateTime.UtcNow
                };
                _db.LotAllocations.Add(allocation);
            }

            result.Processed = true;
            result.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Processed auction result: Lot {LotNumber} assigned to Broker {BrokerId} at {Price} EUR",
                result.LotNumber, result.BrokerId, result.PriceEur);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process auction result for lot {LotNumber}", result.LotNumber);
        }
    }

    private static object ProjectResult(AuctionResult r) => new
    {
        r.Id,
        r.LotNumber,
        r.BrokerId,
        brokerName = r.Broker?.CompanyName,
        brokerNumber = r.Broker?.BrokerNumber,
        r.PriceEur,
        r.SalesType,
        r.Gender,
        r.Group,
        r.Color,
        r.Quality,
        r.Size,
        r.Clarity,
        r.HairLength,
        r.TotalSkins,
        r.BoxCount,
        r.Processed,
        r.ReceivedAt,
        r.ProcessedAt,
        r.SoldToBuyerId,
        soldToBuyerName = r.SoldToBuyer?.Name,
        soldToBuyerNumber = r.SoldToBuyer?.BuyerNumber,
        r.SoldAt,
        r.CommissionType,
        r.CommissionValue,
        r.CommissionAmount,
        r.LastModifiedBy,
        r.LastModifiedAt
    };

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("GetAuctionResults")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results")] HttpRequestData req)
    {
        var results = await _db.AuctionResults
            .OrderByDescending(r => r.ReceivedAt)
            .Select(r => new
            {
                r.Id, r.LotNumber, r.BrokerId,
                brokerName = r.Broker.CompanyName,
                brokerNumber = r.Broker.BrokerNumber,
                r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
                r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
                r.Processed, r.ReceivedAt, r.ProcessedAt,
                r.SoldToBuyerId,
                soldToBuyerName = r.SoldToBuyer != null ? r.SoldToBuyer.Name : null,
                soldToBuyerNumber = r.SoldToBuyer != null ? r.SoldToBuyer.BuyerNumber : null,
                r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
                r.LastModifiedBy, r.LastModifiedAt
            })
            .ToListAsync();

        var allLotNumbers = results.Select(r => r.LotNumber).Distinct().ToList();
        var allLotShipmentStatus = await GetLotShipmentStatusAsync(allLotNumbers);

        var enrichedAll = results.Select(r => new
        {
            r.Id, r.LotNumber, r.BrokerId,
            r.brokerName, r.brokerNumber,
            r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
            r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
            r.Processed, r.ReceivedAt, r.ProcessedAt,
            r.SoldToBuyerId, r.soldToBuyerName, r.soldToBuyerNumber,
            r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
            r.LastModifiedBy, r.LastModifiedAt,
            ShippingStatus = allLotShipmentStatus.GetValueOrDefault(r.LotNumber)
        });

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(enrichedAll, JsonOptions));
        return response;
    }

    [Function("GetAuctionResultsByBroker")]
    public async Task<HttpResponseData> GetByBroker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        if (!req.FunctionContext.CanAccessBroker(brokerId))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        // Optional auction scope: the broker UI works one auction at a time, so callers pass
        // ?auctionId=N to avoid pulling the broker's entire cross-auction history into the browser.
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        int? auctionId = int.TryParse(query["auctionId"], out var aid) ? aid : null;

        var results = await _db.AuctionResults
            .Where(r => r.BrokerId == brokerId && (auctionId == null || r.AuctionId == auctionId.Value))
            .OrderByDescending(r => r.ReceivedAt)
            .Select(r => new
            {
                r.Id, r.AuctionId, r.LotNumber, r.BrokerId,
                brokerName = r.Broker.CompanyName,
                brokerNumber = r.Broker.BrokerNumber,
                r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
                r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
                r.Processed, r.ReceivedAt, r.ProcessedAt,
                r.SoldToBuyerId,
                soldToBuyerName = r.SoldToBuyer != null ? r.SoldToBuyer.Name : null,
                soldToBuyerNumber = r.SoldToBuyer != null ? r.SoldToBuyer.BuyerNumber : null,
                r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
                r.LastModifiedBy, r.LastModifiedAt
            })
            .ToListAsync();

        // Get shipping status for lots
        var lotNumbers = results.Select(r => r.LotNumber).Distinct().ToList();
        var lotShipmentStatus = await GetLotShipmentStatusAsync(lotNumbers);

        // A lot only counts as truly "Invoiced" once its invoice actually posted to BC (has a BC
        // number). Sold lots whose invoice hasn't posted are surfaced as "Not Posted", so a failed
        // BC push never looks like a completed invoice.
        var resultIds = results.Select(r => r.Id).ToList();
        var postedResultIds = (await _db.InvoiceLines
            .Where(l => resultIds.Contains(l.AuctionResultId)
                        && !l.Invoice.IsCreditNote
                        && l.Invoice.BcInvoiceNumber != null && l.Invoice.BcInvoiceNumber != "")
            .Select(l => l.AuctionResultId)
            .Distinct()
            .ToListAsync()).ToHashSet();

        var enriched = results.Select(r => new
        {
            r.Id, r.AuctionId, r.LotNumber, r.BrokerId,
            r.brokerName, r.brokerNumber,
            r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
            r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
            r.Processed, r.ReceivedAt, r.ProcessedAt,
            r.SoldToBuyerId, r.soldToBuyerName, r.soldToBuyerNumber,
            r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
            r.LastModifiedBy, r.LastModifiedAt,
            ShippingStatus = lotShipmentStatus.GetValueOrDefault(r.LotNumber),
            InvoicePosted = postedResultIds.Contains(r.Id)
        });

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(enriched, JsonOptions));
        return response;
    }

    [Function("GetNextUnsoldLot")]
    public async Task<HttpResponseData> GetNextUnsoldLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results/next-unsold-lot")] HttpRequestData req)
    {
        var nextLot = await _db.Lots
            .Where(l => l.Status == LotStatus.Pending || l.Status == LotStatus.Active)
            .OrderBy(l => l.LotNumber)
            .Select(l => new
            {
                l.LotNumber,
                l.Description,
                l.Category,
                l.Quantity,
                l.Unit
            })
            .FirstOrDefaultAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(nextLot, JsonOptions));
        return response;
    }

    [Function("SellLotsToBuyer")]
    public async Task<HttpResponseData> SellToBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results/sell-to-buyer")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SellToBuyerRequest>();
        if (body == null || body.AuctionResultIds == null || body.AuctionResultIds.Count == 0 || body.BuyerId <= 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        // Commission (percentage or fixed amount) can never be negative — reject server-side so a
        // tampered or stale client can't push a negative that would credit the buyer.
        if (body.CommissionValue.HasValue && body.CommissionValue.Value < 0)
        {
            var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "Commission cannot be negative." }, JsonOptions));
            return resp;
        }

        var buyer = await _db.Buyers.FindAsync(body.BuyerId);
        if (buyer == null)
        {
            var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "Buyer not found" }, JsonOptions));
            return resp;
        }

        var results = await _db.AuctionResults
            .Where(r => body.AuctionResultIds.Contains(r.Id) && r.SoldToBuyerId == null)
            .ToListAsync();

        // Bulletproof: a lot may only be sold to a customer LINKED to that lot's broker
        // (the BrokerBuyers junction). Enforced server-side so a tampered/stale client can't
        // sell to a non-linked buyer even if the dropdown were bypassed.
        var brokerIds = results.Select(r => r.BrokerId).Distinct().ToList();
        // Ownership: a broker may only sell their own lots (admins bypass).
        if (brokerIds.Any(bid => !req.FunctionContext.CanAccessBroker(bid)))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);
        if (brokerIds.Count > 0)
        {
            var linkedBrokerIds = await _db.BrokerBuyers
                .Where(bb => bb.BuyerId == body.BuyerId && brokerIds.Contains(bb.BrokerId))
                .Select(bb => bb.BrokerId)
                .Distinct()
                .ToListAsync();
            if (brokerIds.Except(linkedBrokerIds).Any())
            {
                var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                resp.Headers.Add("Content-Type", "application/json");
                await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "Buyer is not a linked customer of the broker for one or more of these lots." }, JsonOptions));
                return resp;
            }
        }

        // Atomically claim each still-unsold lot for this buyer. The guard "WHERE SoldToBuyerId
        // IS NULL" makes the write itself the gate: if two requests (e.g. the same broker logged
        // in twice) try to sell the same lot to different buyers at the same time, exactly one
        // UPDATE matches and the other affects 0 rows — so a lot can never be sold, or invoiced,
        // twice. Only the rows we actually won proceed to history + invoicing below.
        // Sell through the shared core (atomic claim -> commission -> lot status -> history -> invoice
        // -> BC enqueue) — the SAME path the simulator drives, so both exercise identical behaviour.
        var (claimedResults, invoiceId, bcError) = await SellResultsCoreAsync(
            results, body.BuyerId, buyer, body.CommissionType, body.CommissionValue, body.Initials);

        // Every requested lot was already sold by a concurrent request — nothing to do.
        if (claimedResults.Count == 0)
        {
            var conflict = req.CreateResponse(System.Net.HttpStatusCode.Conflict);
            conflict.Headers.Add("Content-Type", "application/json");
            await conflict.WriteStringAsync(JsonSerializer.Serialize(new { error = "These lots were just sold by someone else. Please refresh." }, JsonOptions));
            return conflict;
        }

        var pdfUrl = invoiceId != null ? (await _db.Invoices.FindAsync(invoiceId))?.PdfUrl : null;

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { soldCount = claimedResults.Count, invoiceId, pdfUrl, bcError }, JsonOptions));
        return response;
    }

    // Shared sell core used by BOTH the broker SellLotsToBuyer endpoint and the sell simulator, so they
    // exercise identical behaviour. Atomically claims each still-unsold result for the buyer (with
    // commission), marks its lot Sold, records sales history, then creates the invoice. The atomic
    // "WHERE SoldToBuyerId IS NULL" guard means a lot can never be sold or invoiced twice. Invoicing is
    // NOT gated on BC — a sale always bills locally; the BC push is gated on the queue inside the helper.
    private async Task<(List<AuctionResult> Claimed, int? InvoiceId, string? BcError)> SellResultsCoreAsync(
        List<AuctionResult> results, int buyerId, Buyer buyer, string? commissionType, decimal? commissionValue, string? initials)
    {
        var now = DateTime.UtcNow;
        var claimedResults = new List<AuctionResult>();
        foreach (var result in results)
        {
            decimal? commissionAmount = null;
            if (commissionType == "percentage" && commissionValue.HasValue)
                commissionAmount = (result.TotalSkins * result.PriceEur) * commissionValue.Value / 100m;
            else if (commissionType == "amount" && commissionValue.HasValue)
                commissionAmount = commissionValue.Value;

            var claimed = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE auction.AuctionResults
                SET SoldToBuyerId = {buyerId}, SoldAt = {now}, CommissionType = {commissionType},
                    CommissionValue = {commissionValue}, CommissionAmount = {commissionAmount},
                    LastModifiedBy = {initials}, LastModifiedAt = {now}
                WHERE Id = {result.Id} AND SoldToBuyerId IS NULL");
            if (claimed == 1)
            {
                result.SoldToBuyerId = buyerId; result.SoldAt = now; result.CommissionType = commissionType;
                result.CommissionValue = commissionValue; result.CommissionAmount = commissionAmount;
                result.LastModifiedBy = initials; result.LastModifiedAt = now;
                _db.Entry(result).State = EntityState.Unchanged;
                claimedResults.Add(result);
            }
        }

        if (claimedResults.Count == 0) return (claimedResults, null, null);

        var lotNumbers = claimedResults.Select(r => r.LotNumber).ToList();
        var lots = await _db.Lots.Where(l => lotNumbers.Contains(l.LotNumber)).ToListAsync();
        foreach (var lot in lots) lot.Status = LotStatus.Sold;
        await _db.SaveChangesAsync();

        foreach (var result in claimedResults)
        {
            _db.LotSalesHistories.Add(new LotSalesHistory
            {
                LotNumber = result.LotNumber, AuctionResultId = result.Id, ActionType = "Sold",
                Initials = initials, BuyerId = buyerId, BuyerName = buyer.Name,
                Amount = result.TotalSkins * result.PriceEur, CreatedAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();

        var (invoiceId, bcError) = await CreateInvoiceAndQueuePushAsync(claimedResults, buyerId, buyer);

        if (invoiceId != null)
        {
            var inv = await _db.Invoices.FindAsync(invoiceId);
            var lotNums = claimedResults.Select(r => r.LotNumber).ToList();
            var historyEntries = await _db.LotSalesHistories
                .Where(h => lotNums.Contains(h.LotNumber) && h.InvoiceId == null && h.ActionType == "Sold")
                .OrderByDescending(h => h.CreatedAt)
                .ToListAsync();
            foreach (var h in historyEntries)
            {
                h.InvoiceId = invoiceId;
                h.InvoiceNumber = inv?.InvoiceNumber ?? inv?.BcInvoiceNumber;
            }
            await _db.SaveChangesAsync();
        }

        return (claimedResults, invoiceId, bcError);
    }

    private async Task<(int? InvoiceId, string? BcError)> CreateInvoiceAndQueuePushAsync(List<AuctionResult> results, int buyerId, Buyer buyer)
    {
        try
        {
            // The sell path writes the sale (incl. CommissionAmount) via a raw SQL UPDATE and then
            // marks the tracked entities Unchanged. That can leave CommissionAmount stale (null) on
            // the in-memory objects even though the DB row is correct — which silently dropped the
            // commission line from the invoice. Re-read the authoritative values (AsNoTracking, so we
            // bypass the stale tracked instances) and build the invoice from those.
            var resultIds = results.Select(r => r.Id).ToList();
            results = await _db.AuctionResults.AsNoTracking()
                .Where(r => resultIds.Contains(r.Id))
                .ToListAsync();

            var brokerId = results.First().BrokerId;
            var auctionFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "AuctionFee");
            var handlingFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "HandlingFee");
            var auctionFeePercent = auctionFeeParam != null ? decimal.Parse(auctionFeeParam.Value, CultureInfo.InvariantCulture) : 0m;
            var handlingFeePerSkin = handlingFeeParam != null ? decimal.Parse(handlingFeeParam.Value, CultureInfo.InvariantCulture) : 0m;

            var invoice = new Invoice
            {
                InvoiceNumber = "",
                InvoiceDate = DateTime.UtcNow,
                BrokerId = brokerId,
                BuyerId = buyerId,
                Status = InvoiceStatus.Issued
            };

            decimal subTotal = 0;
            decimal totalAuctionFee = 0;
            decimal totalCommission = 0;

            foreach (var r in results)
            {
                var hammerPrice = r.TotalSkins * r.PriceEur;
                var handlingFee = r.TotalSkins * handlingFeePerSkin;
                var lotAuctionFee = (hammerPrice + handlingFee) * auctionFeePercent / 100m;
                var description = string.Join(", ", new[] { r.SalesType, r.Gender, r.Group, r.Color, r.Quality, r.Size }.Where(s => !string.IsNullOrEmpty(s)));

                invoice.Lines.Add(new InvoiceLine
                {
                    LotNumber = r.LotNumber,
                    Description = description,
                    Skins = r.TotalSkins,
                    PricePerSkin = r.PriceEur,
                    HammerPrice = hammerPrice,
                    AuctionResultId = r.Id
                });

                subTotal += hammerPrice;
                totalAuctionFee += lotAuctionFee;
                totalCommission += r.CommissionAmount ?? 0;
            }

            invoice.SubTotal = subTotal;
            invoice.AuctionFee = totalAuctionFee;
            invoice.Commission = totalCommission;
            invoice.TotalAmount = subTotal + totalAuctionFee + totalCommission;
            invoice.Buyer = buyer;

            _db.Invoices.Add(invoice);
            await _db.SaveChangesAsync();

            // No inline BC push: the serial BcPushDrainer (singleton timer, one doc at a time) picks this
            // up by its empty BcInvoiceNumber and posts it with confirmation. Pushing here would put a
            // second concurrent consumer on BC — exactly the deadlock/empty-post source we removed. The
            // invoice shows "Not Posted" until the drainer confirms it in BC.
            return (invoice.Id, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create BC invoice for {Count} lots", results.Count);
            return (null, ex.Message);
        }
    }

    private record SimulateSellRequest(int AuctionId, int? Count, decimal? MinCommission, decimal? MaxCommission, List<int>? BuyerIds, int? ReinvoicePercent, int? MaxLotsPerInvoice);

    // TEST TOOL (admin only). Sells an auction's recorded-but-unsold lots through the real sell path
    // (atomic claim + commission + invoice + background BC push) so the whole sell -> invoice -> BC ->
    // balance chain can be exercised on demand. Each lot goes to a random buyer LINKED to its broker
    // (the real rule); an optional buyer-id set restricts the pool. Commission % is random in a range.
    // Lots whose broker has no eligible linked buyer are skipped and reported.
    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("SimulateSellLots")]
    public async Task<HttpResponseData> SimulateSell(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results/simulate-sell")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SimulateSellRequest>();
        if (body == null || body.AuctionId <= 0)
            return await SimJson(req, System.Net.HttpStatusCode.BadRequest, new { error = "auctionId is required." });

        // Selling concurrently with the typist sim is ALLOWED (real-life scenario: sell already-typed lots
        // while later lots are still being typed). The double-type race this used to block against (sell DB
        // load slows a typist batch past the queue visibility timeout -> redelivery -> a lot typed twice) is
        // now prevented structurally by the filtered unique index UX_TypistEntries_Auction_Lot_Slot_Active:
        // the redelivered worker hits the constraint, throws, and is detached, so the lot keeps its first
        // result. The index is the real protection; this guard was belt-and-suspenders and is removed.

        var minC = Math.Max(0m, body.MinCommission ?? 0m);
        var maxC = Math.Max(minC, body.MaxCommission ?? minC);
        var maxPerInvoice = Math.Clamp(body.MaxLotsPerInvoice ?? 10, 1, 100);

        // Hard server cap per call so a big auction can't blow the request timeout (the UI loops to
        // continue). Count, when given, is the per-call batch the UI asks for; it's still capped here.
        const int MaxPerCall = 200;
        var take = body.Count is int cap && cap > 0 ? Math.Min(cap, MaxPerCall) : MaxPerCall;

        // A lot can only sell to a buyer LINKED to its winning broker (optionally restricted to a chosen
        // buyer set). Most brokers have no linked buyer, so pick lots ONLY from brokers that DO — otherwise
        // "Lots to sell = N" grabs N lots in lot-number order, skips the unsellable ones, and sells far
        // fewer than asked (the "asked for 10, got 2" case).
        var restrict = body.BuyerIds is { Count: > 0 } ? body.BuyerIds.ToHashSet() : null;
        var sellableBrokers = await _db.BrokerBuyers
            .Where(bb => restrict == null || restrict.Contains(bb.BuyerId))
            .Select(bb => bb.BrokerId).Distinct().ToListAsync();

        var results = await _db.AuctionResults
            .Where(r => r.AuctionId == body.AuctionId && r.SoldToBuyerId == null && sellableBrokers.Contains(r.BrokerId))
            .OrderBy(r => r.LotNumber)
            .Take(take)
            .ToListAsync();
        if (results.Count == 0)
            return await SimJson(req, System.Net.HttpStatusCode.OK, new { auctionId = body.AuctionId, sold = 0, invoices = 0, skipped = 0, reinvoiced = 0, creditNotes = 0, remaining = 0,
                message = sellableBrokers.Count == 0
                    ? "No lots can sell — no buyers are linked to any winning broker (in the selected set). Link customers first."
                    : "No recorded-but-unsold lots left to sell (for brokers that have a linked buyer)." });

        // Linked buyers per picked broker, for buyer assignment in the batch.
        var brokerIds = results.Select(r => r.BrokerId).Distinct().ToList();
        var links = await _db.BrokerBuyers.Where(bb => brokerIds.Contains(bb.BrokerId))
            .Select(bb => new { bb.BrokerId, bb.BuyerId }).ToListAsync();
        var buyersByBroker = links
            .Where(l => restrict == null || restrict.Contains(l.BuyerId))
            .GroupBy(l => l.BrokerId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.BuyerId).Distinct().ToList());
        var allBuyerIds = buyersByBroker.Values.SelectMany(v => v).Distinct().ToList();
        var buyers = await _db.Buyers.Where(b => allBuyerIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id);

        var rnd = new Random();
        var (soldIds, invoices, skipped) = await SimSellBatchAsync(results, buyersByBroker, buyers, minC, maxC, maxPerInvoice, rnd);
        if (soldIds.Count == 0)
            return await SimJson(req, System.Net.HttpStatusCode.OK, new { auctionId = body.AuctionId, sold = 0, invoices = 0, skipped, reinvoiced = 0, creditNotes = 0,
                message = skipped > 0 ? "No lots sold — the winning brokers have no linked buyer in the selected set. Link customers first." : "No lots sold." });

        // Optionally take back a % of the just-sold lots through the real credit-note path (-> BC credit
        // memo) and sell them again, exercising the whole re-invoice / credit-memo chain end to end.
        int reinvoiced = 0, creditNotes = 0, reinvoiceInvoices = 0;
        var reinvPct = Math.Clamp(body.ReinvoicePercent ?? 0, 0, 100);
        if (reinvPct > 0)
        {
            int targetLots = (int)Math.Ceiling(soldIds.Count * reinvPct / 100.0);

            // Map the just-sold lots to their invoices so credits vary realistically: each chosen invoice
            // is either fully credited (all its lots taken back) or partially (a random subset of its lots).
            var lines = await _db.InvoiceLines.Where(l => soldIds.Contains(l.AuctionResultId))
                .Select(l => new { l.InvoiceId, l.AuctionResultId }).ToListAsync();
            var invoiceGroups = lines.GroupBy(x => x.InvoiceId).Select(g => g.Select(x => x.AuctionResultId).ToList()).ToList();

            var pickIds = new List<int>();
            foreach (var invLots in invoiceGroups.OrderBy(_ => rnd.Next()))
            {
                if (pickIds.Count >= targetLots) break;
                if (rnd.Next(2) == 0)                 // whole invoice
                    pickIds.AddRange(invLots);
                else                                  // a random subset of this invoice's lots
                    pickIds.AddRange(invLots.OrderBy(_ => rnd.Next()).Take(rnd.Next(1, invLots.Count + 1)));
            }

            var toReinvoice = await _db.AuctionResults.Where(r => pickIds.Contains(r.Id) && r.SoldToBuyerId != null).ToListAsync();
            if (toReinvoice.Count > 0)
            {
                foreach (var r in toReinvoice) { r.LastModifiedBy = "SIM"; r.LastModifiedAt = DateTime.UtcNow; }
                var (_, takenBackIds, _) = await ProcessBrokerTakebacksAsync(toReinvoice);
                await _db.SaveChangesAsync();
                var cns = await GenerateCreditNotesAsync(takenBackIds);
                creditNotes = cns.Count;
                reinvoiced = takenBackIds.Count;

                var reSell = await _db.AuctionResults.Where(r => takenBackIds.Contains(r.Id) && r.SoldToBuyerId == null).ToListAsync();
                var (_, inv2, _) = await SimSellBatchAsync(reSell, buyersByBroker, buyers, minC, maxC, maxPerInvoice, rnd);
                reinvoiceInvoices = inv2;
            }
        }

        var remaining = await _db.AuctionResults.CountAsync(r => r.AuctionId == body.AuctionId && r.SoldToBuyerId == null);
        return await SimJson(req, System.Net.HttpStatusCode.OK, new { auctionId = body.AuctionId, sold = soldIds.Count, invoices, skipped, reinvoiced, creditNotes, reinvoiceInvoices, remaining });
    }

    // Claim + record + invoice a batch of unsold results. Returns the ids actually sold, the invoice
    // count, and how many were skipped for lack of an eligible linked buyer.
    private async Task<(List<int> SoldIds, int Invoices, int Skipped)> SimSellBatchAsync(
        List<AuctionResult> results, Dictionary<int, List<int>> buyersByBroker, Dictionary<int, Buyer> buyers,
        decimal minC, decimal maxC, int maxPerInvoice, Random rnd)
    {
        decimal RandPct() => maxC <= minC ? minC : Math.Round(minC + (decimal)rnd.NextDouble() * (maxC - minC), 2);

        // Assign each sellable result a random linked buyer of its broker; skip those with no eligible
        // buyer. Then sell through the SHARED SellResultsCoreAsync — the exact path the broker UI uses.
        var assignments = new List<(AuctionResult Result, int BuyerId)>();
        int skipped = 0;
        foreach (var r in results)
        {
            if (!buyersByBroker.TryGetValue(r.BrokerId, out var pool) || pool.Count == 0) { skipped++; continue; }
            assignments.Add((r, pool[rnd.Next(pool.Count)]));
        }
        if (assignments.Count == 0) return (new List<int>(), 0, skipped);

        var soldIds = new List<int>();
        int invoices = 0;
        // Group by (broker, buyer), split each into invoices of a RANDOM number of lots (1..maxPerInvoice),
        // and sell each chunk via the real core at a random commission %.
        foreach (var grp in assignments.GroupBy(a => new { a.Result.BrokerId, a.BuyerId }))
        {
            var groupResults = grp.Select(a => a.Result).ToList();
            var buyer = buyers[grp.Key.BuyerId];
            int idx = 0;
            while (idx < groupResults.Count)
            {
                var remaining = groupResults.Count - idx;
                var chunkSize = maxPerInvoice <= 1 ? 1 : rnd.Next(1, Math.Min(maxPerInvoice, remaining) + 1);
                var chunk = groupResults.GetRange(idx, chunkSize);
                idx += chunkSize;
                var (claimed, invId, _) = await SellResultsCoreAsync(chunk, grp.Key.BuyerId, buyer, "percentage", RandPct(), "SIM");
                soldIds.AddRange(claimed.Select(r => r.Id));
                if (invId != null) invoices++;
            }
        }

        return (soldIds, invoices, skipped);
    }

    private static async Task<HttpResponseData> SimJson(HttpRequestData req, System.Net.HttpStatusCode code, object body)
    {
        var resp = req.CreateResponse(code);
        resp.Headers.Add("Content-Type", "application/json");
        await resp.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return resp;
    }

    private record SimulateBrokersRequest(int AuctionId, int? BrokerCount, int? DurationSeconds, decimal? Commission, int? ReinvoicePercent, int? MaxLotsPerInvoice);

    private class BrokerSimResult { public int BrokerId; public int Sold; public int Invoices; public int CreditNotes; public int Reinvoiced; public int Errors; }

    // Concurrency simulator: N brokers selling + re-invoicing their own lots AT THE SAME TIME, to stress
    // the real flow under simultaneous broker activity (atomic claims, invoice creation, the BC drainer,
    // the credit-note path). Each broker runs in its OWN DbContext scope so the parallelism is genuine.
    [Function("SimulateBrokers")]
    public async Task<HttpResponseData> SimulateBrokers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results/simulate-brokers")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SimulateBrokersRequest>();
        if (body == null || body.AuctionId <= 0)
            return await SimJson(req, System.Net.HttpStatusCode.BadRequest, new { error = "auctionId is required." });
        if (_scopeFactory is null)
            return await SimJson(req, System.Net.HttpStatusCode.InternalServerError, new { error = "Broker simulator unavailable (no scope factory)." });

        // Running concurrently with the typist sim is ALLOWED — see the note in SimulateSell. The double-type
        // race is prevented by the UX_TypistEntries_Auction_Lot_Slot_Active unique index, not by blocking here.

        var commission = Math.Max(0m, body.Commission ?? 1.5m);
        var reinvPct = Math.Clamp(body.ReinvoicePercent ?? 0, 0, 100);
        var maxPerInvoice = Math.Clamp(body.MaxLotsPerInvoice ?? 10, 1, 100);
        var durationSec = Math.Clamp(body.DurationSeconds ?? 30, 1, 120);
        var brokerCount = Math.Clamp(body.BrokerCount ?? 15, 1, 50);
        var deadline = DateTime.UtcNow.AddSeconds(durationSec);

        // Brokers that have unsold lots in this auction AND a linked buyer (only those can sell).
        var linkedBrokers = (await _db.BrokerBuyers.Select(bb => bb.BrokerId).Distinct().ToListAsync()).ToHashSet();
        var brokersWithUnsold = await _db.AuctionResults
            .Where(r => r.AuctionId == body.AuctionId && r.SoldToBuyerId == null)
            .Select(r => r.BrokerId).Distinct().ToListAsync();
        var candidates = brokersWithUnsold.Where(linkedBrokers.Contains).Take(brokerCount).ToList();
        if (candidates.Count == 0)
            return await SimJson(req, System.Net.HttpStatusCode.OK, new { auctionId = body.AuctionId, brokers = 0, message = "No brokers with unsold lots and a linked buyer." });

        // Fan out — every broker runs concurrently, each in its own scope/context.
        var results = await Task.WhenAll(candidates.Select(brokerId =>
            RunBrokerInScopeAsync(body.AuctionId, brokerId, commission, reinvPct, maxPerInvoice, deadline)));

        return await SimJson(req, System.Net.HttpStatusCode.OK, new
        {
            auctionId = body.AuctionId,
            brokers = results.Length,
            durationSeconds = durationSec,
            sold = results.Sum(r => r.Sold),
            invoices = results.Sum(r => r.Invoices),
            creditNotes = results.Sum(r => r.CreditNotes),
            reinvoiced = results.Sum(r => r.Reinvoiced),
            errors = results.Sum(r => r.Errors),
            perBroker = results.OrderBy(r => r.BrokerId).Select(r => new { broker = r.BrokerId, r.Sold, r.Invoices, r.CreditNotes, r.Errors })
        });
    }

    // Run one broker's sell/re-invoice loop on a FRESH scope (own DbContext + services), so 15 of these
    // can run truly in parallel without sharing the request's context.
    private async Task<BrokerSimResult> RunBrokerInScopeAsync(int auctionId, int brokerId, decimal commission, int reinvPct, int maxPerInvoice, DateTime deadline)
    {
        try
        {
            using var scope = _scopeFactory!.CreateScope();
            var fn = ActivatorUtilities.CreateInstance<AuctionResultFunctions>(scope.ServiceProvider);
            return await fn.SimulateSellForBrokerAsync(auctionId, brokerId, commission, reinvPct, maxPerInvoice, deadline);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Broker sim failed for broker {Broker}", brokerId);
            return new BrokerSimResult { BrokerId = brokerId, Errors = 1 };
        }
    }

    // Sell (and optionally re-invoice) a single broker's unsold lots, looping until they're exhausted or
    // the shared deadline passes. Uses this instance's (scoped) _db, so it's safe to run concurrently with
    // other brokers' runners. Invoked by RunBrokerInScopeAsync on the per-scope instance (private access
    // to another instance of the same type is allowed in C#).
    private async Task<BrokerSimResult> SimulateSellForBrokerAsync(int auctionId, int brokerId, decimal commission, int reinvPct, int maxPerInvoice, DateTime deadline)
    {
        var res = new BrokerSimResult { BrokerId = brokerId };
        var rnd = new Random(brokerId * 100003 + Environment.TickCount);

        var buyerIds = await _db.BrokerBuyers.Where(bb => bb.BrokerId == brokerId).Select(bb => bb.BuyerId).Distinct().ToListAsync();
        if (buyerIds.Count == 0) return res;
        var buyers = await _db.Buyers.Where(b => buyerIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id);
        var buyersByBroker = new Dictionary<int, List<int>> { [brokerId] = buyerIds };

        while (DateTime.UtcNow < deadline)
        {
            var didWork = false;

            // (a) Sell any unsold lots for this broker.
            var unsold = await _db.AuctionResults
                .Where(r => r.AuctionId == auctionId && r.BrokerId == brokerId && r.SoldToBuyerId == null)
                .OrderBy(r => r.LotNumber).Take(40).ToListAsync();
            if (unsold.Count > 0)
            {
                var (soldIds, inv, _) = await SimSellBatchAsync(unsold, buyersByBroker, buyers, commission, commission, maxPerInvoice, rnd);
                res.Sold += soldIds.Count;
                res.Invoices += inv;
                didWork |= soldIds.Count > 0;
            }

            // (b) Re-invoice churn: take back a random chunk of this broker's already-SOLD lots, credit
            // them, and re-sell — so the brokers keep stressing the credit-note + re-sell path concurrently
            // even when the auction is fully sold (which is the case after a full sell run).
            if (reinvPct > 0)
            {
                var sold = await _db.AuctionResults
                    .Where(r => r.AuctionId == auctionId && r.BrokerId == brokerId && r.SoldToBuyerId != null)
                    .OrderBy(r => Guid.NewGuid()).Take(40).ToListAsync();
                var pick = sold.Take(Math.Max(1, (int)Math.Ceiling(sold.Count * reinvPct / 100.0))).ToList();
                if (pick.Count > 0)
                {
                    foreach (var r in pick) { r.LastModifiedBy = "BROKERSIM"; r.LastModifiedAt = DateTime.UtcNow; }
                    var (_, takenBackIds, _) = await ProcessBrokerTakebacksAsync(pick);
                    await _db.SaveChangesAsync();
                    res.CreditNotes += (await GenerateCreditNotesAsync(takenBackIds)).Count;
                    res.Reinvoiced += takenBackIds.Count;
                    var reSell = await _db.AuctionResults.Where(r => takenBackIds.Contains(r.Id) && r.SoldToBuyerId == null).ToListAsync();
                    var (_, inv2, _) = await SimSellBatchAsync(reSell, buyersByBroker, buyers, commission, commission, maxPerInvoice, rnd);
                    res.Invoices += inv2;
                    didWork |= takenBackIds.Count > 0;
                }
            }

            if (!didWork) break; // nothing left to sell and nothing to re-invoice
        }
        return res;
    }

    [Function("GetAuctionResultsByBuyer")]
    public async Task<HttpResponseData> GetByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results/buyer/{buyerId:int}")] HttpRequestData req, int buyerId)
    {
        if (!req.FunctionContext.CanAccessBuyer(buyerId))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        // Optional auction scope (?auctionId=N): the buyer purchases page defaults to one auction
        // so it no longer pulls the buyer's entire purchase history into the browser.
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        int? auctionId = int.TryParse(query["auctionId"], out var aid) ? aid : null;

        var results = await _db.AuctionResults
            .Where(r => r.SoldToBuyerId == buyerId && (auctionId == null || r.AuctionId == auctionId.Value))
            .OrderByDescending(r => r.SoldAt)
            .Select(r => new
            {
                r.Id, r.LotNumber, r.BrokerId,
                brokerName = r.Broker.CompanyName,
                brokerNumber = r.Broker.BrokerNumber,
                r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
                r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
                r.Processed, r.ReceivedAt, r.ProcessedAt,
                r.SoldToBuyerId,
                soldToBuyerName = r.SoldToBuyer != null ? r.SoldToBuyer.Name : null,
                soldToBuyerNumber = r.SoldToBuyer != null ? r.SoldToBuyer.BuyerNumber : null,
                r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
                r.LastModifiedBy, r.LastModifiedAt
            })
            .ToListAsync();

        var buyerLotNumbers = results.Select(r => r.LotNumber).Distinct().ToList();
        var buyerLotShipmentStatus = await GetLotShipmentStatusAsync(buyerLotNumbers);

        var enrichedBuyer = results.Select(r => new
        {
            r.Id, r.LotNumber, r.BrokerId,
            r.brokerName, r.brokerNumber,
            r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
            r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
            r.Processed, r.ReceivedAt, r.ProcessedAt,
            r.SoldToBuyerId, r.soldToBuyerName, r.soldToBuyerNumber,
            r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
            r.LastModifiedBy, r.LastModifiedAt,
            ShippingStatus = buyerLotShipmentStatus.GetValueOrDefault(r.LotNumber)
        });

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(enrichedBuyer, JsonOptions));
        return response;
    }
    [Function("RequestTakeback")]
    public async Task<HttpResponseData> RequestTakeback(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "takeback-requests")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<TakebackRequestBody>();
        if (body == null || body.AuctionResultIds == null || body.AuctionResultIds.Count == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var results = await _db.AuctionResults
            .Where(r => body.AuctionResultIds.Contains(r.Id) && r.SoldToBuyerId != null)
            .ToListAsync();

        // Ownership: a broker may only take back their own lots (admins bypass).
        if (results.Select(r => r.BrokerId).Distinct().Any(bid => !req.FunctionContext.CanAccessBroker(bid)))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        var shippingError = await CheckShippedLotsAsync(results, req, "Cannot take back");
        if (shippingError != null) return shippingError;

        // Record initials on auction results
        foreach (var r in results)
        {
            r.LastModifiedBy = body.Initials;
            r.LastModifiedAt = DateTime.UtcNow;
        }

        var (created, takenBackResultIds, _) = await ProcessBrokerTakebacksAsync(results);
        await _db.SaveChangesAsync();

        // Issue one credit note per buyer/original invoice for the taken-back lots.
        var creditNotes = new List<Invoice>();
        try
        {
            creditNotes = await GenerateCreditNotesAsync(takenBackResultIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate credit notes for takeback");
        }

        // Map each taken-back auction result to the credit note that actually covers it,
        // so history is attributed to the lot's own buyer — not the first buyer in the batch.
        var creditNoteByResultId = new Dictionary<int, Invoice>();
        foreach (var cn in creditNotes)
            foreach (var line in cn.Lines)
                creditNoteByResultId[line.AuctionResultId] = cn;

        // Record re-invoice history per lot
        foreach (var r in results)
        {
            creditNoteByResultId.TryGetValue(r.Id, out var cn);
            _db.LotSalesHistories.Add(new LotSalesHistory
            {
                LotNumber = r.LotNumber,
                AuctionResultId = r.Id,
                ActionType = "Re-Invoice",
                Initials = body.Initials,
                BuyerId = cn?.BuyerId,
                InvoiceId = cn?.Id,
                InvoiceNumber = cn?.InvoiceNumber ?? cn?.BcInvoiceNumber,
                Amount = r.TotalSkins * r.PriceEur,
                CreatedAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();

        var firstCreditNote = creditNotes.FirstOrDefault();
        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            requestCount = created.Count,
            creditNoteId = firstCreditNote?.Id,
            creditNotePdfUrl = firstCreditNote?.PdfUrl,
            creditNoteIds = creditNotes.Select(c => c.Id).ToList()
        }, JsonOptions));
        return response;
    }

    [Function("GetLotSalesHistory")]
    public async Task<HttpResponseData> GetLotSalesHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results/lot-history/{lotNumber:int}")] HttpRequestData req, int lotNumber)
    {
        var history = await _db.LotSalesHistories
            .Where(h => h.LotNumber == lotNumber)
            .OrderByDescending(h => h.CreatedAt)
            .Select(h => new
            {
                h.Id, h.LotNumber, h.ActionType, h.Initials,
                h.BuyerId, h.BuyerName,
                h.InvoiceId, h.InvoiceNumber,
                h.Amount, h.Notes, h.CreatedAt
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(history, JsonOptions));
        return response;
    }

    [Function("RequestTakebackByBuyer")]
    public async Task<HttpResponseData> RequestTakebackByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "takeback-requests/buyer-initiated")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<TakebackRequestBody>();
        if (body == null || body.AuctionResultIds == null || body.AuctionResultIds.Count == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var results = await _db.AuctionResults
            .Where(r => body.AuctionResultIds.Contains(r.Id) && r.SoldToBuyerId != null)
            .ToListAsync();

        // Ownership: a buyer may only request return of their own purchases (admins bypass).
        if (results.Select(r => r.SoldToBuyerId!.Value).Distinct().Any(bid => !req.FunctionContext.CanAccessBuyer(bid)))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        var shippingError = await CheckShippedLotsAsync(results, req, "Cannot request return for");
        if (shippingError != null) return shippingError;

        var created = await ProcessBuyerTakebackRequestsAsync(results);
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { requestCount = created.Count }, JsonOptions));
        return response;
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("ReassignUnsoldLots")]
    public async Task<HttpResponseData> ReassignUnsoldLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results/reassign-unsold")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<ReassignUnsoldRequest>();
        if (body == null || body.AuctionResultIds == null || body.AuctionResultIds.Count == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var results = await _db.AuctionResults
            .Where(r => body.AuctionResultIds.Contains(r.Id) && r.SoldToBuyerId == null)
            .ToListAsync();

        if (results.Count == 0)
        {
            var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "No unsold lots found for given IDs" }, JsonOptions));
            return resp;
        }

        int reassigned;
        if (body.Action == "internal")
            reassigned = await ReassignToInternalBrokerAsync(results);
        else if (body.Action == "auction")
            reassigned = await ReturnLotsToAuctionAsync(results);
        else
        {
            var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "Invalid action. Use 'internal' or 'auction'." }, JsonOptions));
            return resp;
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { action = body.Action, reassigned }, JsonOptions));
        return response;
    }

    private async Task<int> ReassignToInternalBrokerAsync(List<AuctionResult> results)
    {
        var internalBroker = await _db.Brokers.FirstOrDefaultAsync(b => b.BrokerNumber == "999");
        if (internalBroker == null)
        {
            internalBroker = new Broker { BrokerNumber = "999", CompanyName = "Internal", IsActive = true };
            _db.Brokers.Add(internalBroker);
            await _db.SaveChangesAsync();
        }

        foreach (var result in results)
            result.BrokerId = internalBroker.Id;

        return results.Count;
    }

    private async Task<int> ReturnLotsToAuctionAsync(List<AuctionResult> results)
    {
        foreach (var result in results)
        {
            var lot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == result.LotNumber);
            if (lot != null) lot.Status = LotStatus.Unsold;
            _db.AuctionResults.Remove(result);
        }
        return results.Count;
    }

    [Function("GetTakebackRequestsByBuyer")]
    public async Task<HttpResponseData> GetTakebacksByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "takeback-requests/buyer/{buyerId:int}")] HttpRequestData req, int buyerId)
    {
        if (!req.FunctionContext.CanAccessBuyer(buyerId))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        var requests = await _db.TakebackRequests
            .Include(t => t.AuctionResult)
            .Include(t => t.Broker)
            .Include(t => t.Buyer)
            .Where(t => t.BuyerId == buyerId)
            .OrderByDescending(t => t.RequestedAt)
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(requests.Select(t => new
        {
            t.Id,
            t.AuctionResultId,
            lotNumber = t.AuctionResult.LotNumber,
            salesType = t.AuctionResult.SalesType,
            color = t.AuctionResult.Color,
            totalSkins = t.AuctionResult.TotalSkins,
            priceEur = t.AuctionResult.PriceEur,
            brokerName = t.Broker.CompanyName,
            brokerNumber = t.Broker.BrokerNumber,
            t.InitiatedBy,
            t.Status,
            t.RequestedAt,
            t.RespondedAt
        }), JsonOptions));
        return response;
    }

    [Function("GetTakebackRequestsByBroker")]
    public async Task<HttpResponseData> GetTakebacksByBroker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "takeback-requests/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        if (!req.FunctionContext.CanAccessBroker(brokerId))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        var requests = await _db.TakebackRequests
            .Include(t => t.AuctionResult).ThenInclude(a => a.SoldToBuyer)
            .Include(t => t.Broker)
            .Include(t => t.Buyer)
            .Where(t => t.BrokerId == brokerId)
            .OrderByDescending(t => t.RequestedAt)
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(requests.Select(t => new
        {
            t.Id,
            t.AuctionResultId,
            lotNumber = t.AuctionResult.LotNumber,
            salesType = t.AuctionResult.SalesType,
            gender = t.AuctionResult.Gender,
            color = t.AuctionResult.Color,
            quality = t.AuctionResult.Quality,
            totalSkins = t.AuctionResult.TotalSkins,
            priceEur = t.AuctionResult.PriceEur,
            buyerName = t.Buyer.Name,
            buyerNumber = t.Buyer.BuyerNumber,
            t.InitiatedBy,
            t.Status,
            t.RequestedAt,
            t.RespondedAt
        }), JsonOptions));
        return response;
    }

    [Function("RespondTakebackRequest")]
    public async Task<HttpResponseData> RespondTakeback(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "takeback-requests/{id:int}")] HttpRequestData req, int id)
    {
        var body = await req.ReadFromJsonAsync<TakebackResponseBody>();
        if (body == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var takebackReq = await _db.TakebackRequests
            .Include(t => t.AuctionResult)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (takebackReq == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // Ownership: only the owning broker, the owning buyer, or an admin may respond.
        if (!req.FunctionContext.CanAccessBroker(takebackReq.BrokerId) && !req.FunctionContext.CanAccessBuyer(takebackReq.BuyerId))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        if (takebackReq.Status != CustomerRequestStatus.Pending)
        {
            var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "Request already responded to" }, JsonOptions));
            return resp;
        }

        takebackReq.Status = body.Approve ? CustomerRequestStatus.Approved : CustomerRequestStatus.Declined;
        takebackReq.RespondedAt = DateTime.UtcNow;

        int? creditNoteId = null;
        var resultClaimed = false;
        if (body.Approve)
        {
            // Atomically take the result back. Only the responder that flips SoldToBuyerId
            // (NOT NULL -> NULL) credits it, so two concurrent approves — or a broker re-invoice
            // racing this — can't both generate a credit note for the same result.
            var claimed = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE auction.AuctionResults
                SET SoldToBuyerId = NULL, SoldAt = NULL
                WHERE Id = {takebackReq.AuctionResultId} AND SoldToBuyerId IS NOT NULL");
            resultClaimed = claimed > 0;

            takebackReq.AuctionResult.SoldToBuyerId = null;
            takebackReq.AuctionResult.SoldAt = null;

            // Update lot status back to Broker
            var lot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == takebackReq.AuctionResult.LotNumber);
            if (lot != null) lot.Status = LotStatus.Broker;
        }

        await _db.SaveChangesAsync();

        if (body.Approve && resultClaimed)
        {
            try
            {
                var creditNotes = await GenerateCreditNotesAsync(new List<int> { takebackReq.AuctionResultId });
                creditNoteId = creditNotes.Count > 0 ? creditNotes[0].Id : (int?)null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate credit note for takeback approval");
            }
        }
        else if (body.Approve)
        {
            _logger.LogWarning("Takeback {Id}: result {ResultId} was already taken back by a concurrent request; skipping duplicate credit note",
                takebackReq.Id, takebackReq.AuctionResultId);
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { status = takebackReq.Status.ToString(), creditNoteId }, JsonOptions));
        return response;
    }

    private async Task<HttpResponseData?> CheckShippedLotsAsync(
        List<AuctionResult> results, HttpRequestData req, string errorPrefix)
    {
        var lotNumbers = results.Select(r => r.LotNumber).ToList();

        // Check invoices with shipping status
        var shippedViaInvoice = await _db.Invoices
            .Where(i => i.ShippingStatus == "Released" && !i.IsCreditNote)
            .SelectMany(i => i.Lines)
            .Where(l => lotNumbers.Contains(l.LotNumber))
            .Select(l => l.LotNumber)
            .Distinct()
            .ToListAsync();

        // Also check shipment lines directly
        var shippedViaShipment = await _db.ShipmentLines
            .Where(sl => lotNumbers.Contains(sl.LotNumber))
            .Select(sl => sl.LotNumber)
            .Distinct()
            .ToListAsync();

        var shippedLotNumbers = shippedViaInvoice.Union(shippedViaShipment).Distinct().ToList();

        if (shippedLotNumbers.Count == 0) return null;

        var errorResp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        errorResp.Headers.Add("Content-Type", "application/json");
        await errorResp.WriteStringAsync(JsonSerializer.Serialize(new
        {
            error = "Lot Sold",
            message = $"{errorPrefix} lot(s) {string.Join(", ", shippedLotNumbers)} — already on a shipment order.",
            shippedLotNumbers
        }, JsonOptions));
        return errorResp;
    }

    private async Task<(List<TakebackRequest> Created, List<int> TakenBackResultIds, int? BuyerId)> ProcessBrokerTakebacksAsync(
        List<AuctionResult> results)
    {
        var created = new List<TakebackRequest>();
        var takenBackResultIds = new List<int>();
        int? takebackBuyerId = null;

        foreach (var result in results)
        {
            var existing = await _db.TakebackRequests
                .FirstOrDefaultAsync(t => t.AuctionResultId == result.Id && t.Status == CustomerRequestStatus.Pending);
            if (existing != null) continue;

            // Atomically take the result back: only the request that actually flips SoldToBuyerId
            // (NOT NULL -> NULL) proceeds to credit it. The previous tracked-entity null-out let two
            // concurrent re-invoices of the same lot both pass the "is sold" check and both generate
            // a credit note (duplicate credit). The loser here gets 0 rows and skips.
            var claimed = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE auction.AuctionResults
                SET SoldToBuyerId = NULL, SoldAt = NULL
                WHERE Id = {result.Id} AND SoldToBuyerId IS NOT NULL");
            if (claimed == 0) continue;

            takebackBuyerId ??= result.SoldToBuyerId;

            var takebackReq = new TakebackRequest
            {
                AuctionResultId = result.Id,
                BrokerId = result.BrokerId,
                BuyerId = result.SoldToBuyerId!.Value,
                InitiatedBy = "Broker",
                Status = CustomerRequestStatus.Approved,
                RequestedAt = DateTime.UtcNow,
                RespondedAt = DateTime.UtcNow
            };
            _db.TakebackRequests.Add(takebackReq);
            created.Add(takebackReq);
            takenBackResultIds.Add(result.Id);

            // Keep the tracked entity consistent with the row we just nulled in the DB.
            result.SoldToBuyerId = null;
            result.SoldAt = null;

            var lot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == result.LotNumber);
            if (lot != null) lot.Status = LotStatus.Broker;
        }

        return (created, takenBackResultIds, takebackBuyerId);
    }

    private async Task<List<TakebackRequest>> ProcessBuyerTakebackRequestsAsync(List<AuctionResult> results)
    {
        var created = new List<TakebackRequest>();
        foreach (var result in results)
        {
            var existing = await _db.TakebackRequests
                .FirstOrDefaultAsync(t => t.AuctionResultId == result.Id && t.Status == CustomerRequestStatus.Pending);
            if (existing != null) continue;

            var takebackReq = new TakebackRequest
            {
                AuctionResultId = result.Id,
                BrokerId = result.BrokerId,
                BuyerId = result.SoldToBuyerId!.Value,
                InitiatedBy = "Buyer",
                Status = CustomerRequestStatus.Pending,
                RequestedAt = DateTime.UtcNow
            };
            _db.TakebackRequests.Add(takebackReq);
            created.Add(takebackReq);
        }
        return created;
    }

    // Issue credit notes for the given auction results. Lines are grouped by their ORIGINAL
    // invoice, so taking back lots that were sold to several buyers produces one credit note
    // per buyer/invoice — not a single credit note lumped onto the first buyer (which left the
    // other buyers charged but never credited). Returns the created credit notes.
    private async Task<List<Invoice>> GenerateCreditNotesAsync(List<int> auctionResultIds)
    {
        if (auctionResultIds.Count == 0) return new List<Invoice>();

        var allInvoiceLines = await _db.Set<InvoiceLine>()
            .Include(l => l.Invoice).ThenInclude(i => i.Buyer)
            .Include(l => l.Invoice).ThenInclude(i => i.Broker)
            .Where(l => auctionResultIds.Contains(l.AuctionResultId) && !l.Invoice.IsCreditNote)
            .ToListAsync();

        // Credit the MOST RECENT sale (invoice line) per auction result. A lot can be sold and credited
        // many times over its life, so we always credit the current sale — never an older one.
        var invoiceLines = allInvoiceLines
            .GroupBy(l => l.AuctionResultId)
            .Select(g => g.OrderByDescending(l => l.Invoice.InvoiceDate).First())
            .ToList();

        // Idempotency scoped to the SALE, not the lot: skip only a (result, original-invoice) pair that
        // already has a credit note, so the same sale can't be double-credited — but a lot that was
        // credited, re-sold, and taken back again IS credited again (its NEW invoice isn't in this set).
        // The atomic take-back claim in the callers remains the primary concurrent double-submit defence.
        var creditedPairs = (await _db.Set<InvoiceLine>()
                .Where(l => auctionResultIds.Contains(l.AuctionResultId) && l.Invoice.IsCreditNote && l.Invoice.OriginalInvoiceId != null)
                .Select(l => new { l.AuctionResultId, OriginalInvoiceId = l.Invoice.OriginalInvoiceId!.Value })
                .Distinct()
                .ToListAsync())
            .Select(p => (p.AuctionResultId, p.OriginalInvoiceId))
            .ToHashSet();
        invoiceLines = invoiceLines.Where(l => !creditedPairs.Contains((l.AuctionResultId, l.InvoiceId))).ToList();

        if (invoiceLines.Count == 0) return new List<Invoice>();

        var auctionFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "AuctionFee");
        var handlingFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "HandlingFee");
        var auctionFeePercent = auctionFeeParam != null ? decimal.Parse(auctionFeeParam.Value, CultureInfo.InvariantCulture) : 0m;
        var handlingFeePerSkin = handlingFeeParam != null ? decimal.Parse(handlingFeeParam.Value, CultureInfo.InvariantCulture) : 0m;

        var createdCreditNotes = new List<Invoice>();

        // One credit note per original invoice → the correct buyer + OriginalInvoiceId for each.
        foreach (var grp in invoiceLines.GroupBy(l => l.InvoiceId))
        {
            var originalInvoice = grp.First().Invoice;

            var creditNote = new Invoice
            {
                InvoiceNumber = "",
                InvoiceDate = DateTime.UtcNow,
                BrokerId = originalInvoice.BrokerId,
                BuyerId = originalInvoice.BuyerId,
                IsCreditNote = true,
                OriginalInvoiceId = originalInvoice.Id,
                Status = InvoiceStatus.Issued
            };

            decimal subTotal = 0;
            decimal totalAuctionFee = 0;
            decimal totalCommission = 0;

            foreach (var line in grp)
            {
                var handlingFee = line.Skins * handlingFeePerSkin;
                var lotAuctionFee = (line.HammerPrice + handlingFee) * auctionFeePercent / 100m;

                creditNote.Lines.Add(new InvoiceLine
                {
                    LotNumber = line.LotNumber,
                    Description = line.Description,
                    Skins = -line.Skins,
                    PricePerSkin = line.PricePerSkin,
                    HammerPrice = -line.HammerPrice,
                    AuctionResultId = line.AuctionResultId
                });

                subTotal -= line.HammerPrice;
                totalAuctionFee -= lotAuctionFee;

                var result = await _db.AuctionResults.FindAsync(line.AuctionResultId);
                totalCommission -= result?.CommissionAmount ?? 0;
            }

            creditNote.SubTotal = subTotal;
            creditNote.AuctionFee = totalAuctionFee;
            creditNote.Commission = totalCommission;
            creditNote.TotalAmount = subTotal + totalAuctionFee + totalCommission;

            creditNote.Buyer = originalInvoice.Buyer;
            creditNote.OriginalInvoice = originalInvoice;

            _db.Invoices.Add(creditNote);
            createdCreditNotes.Add(creditNote);
        }

        await _db.SaveChangesAsync();

        // No inline BC push. The serial BcPushDrainer posts these credit notes one at a time (credit
        // notes before invoices each tick), confirming each in BC. Pushing inline here is what hung the
        // request and led to re-clicks + duplicate credit notes; the drainer is the single consumer now.
        return createdCreditNotes;
    }

    [Function("DiagBlobStorage")]
    public async Task<HttpResponseData> DiagBlobStorage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diag/blob")] HttpRequestData req)
    {
        var result = new Dictionary<string, object?>();
        result["blobStorageAvailable"] = _blobStorage != null;
        result["bcSyncServiceAvailable"] = _bcSyncService != null;

        if (_blobStorage != null)
        {
            try
            {
                var testData = System.Text.Encoding.UTF8.GetBytes("blob-storage-test");
                var url = await _blobStorage.UploadPdfAsync("_diag_test.txt", testData);
                result["uploadSuccess"] = true;
                result["uploadUrl"] = url;
            }
            catch (Exception ex)
            {
                result["uploadSuccess"] = false;
                result["uploadError"] = ex.ToString();
            }
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, JsonOptions));
        return response;
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("BackfillCatalogData")]
    public async Task<HttpResponseData> BackfillCatalogData(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results/backfill-catalog")] HttpRequestData req)
    {
        var results = await _db.AuctionResults.Where(r => r.Size == null).ToListAsync();
        var lotNumbers = results.Select(r => r.LotNumber).Distinct().ToList();
        var catalogLots = await _catalogDb.CatalogLots
            .Where(cl => lotNumbers.Contains(cl.LotNumber))
            .ToListAsync();
        var catalogMap = catalogLots.GroupBy(cl => cl.LotNumber).ToDictionary(g => g.Key, g => g.First());

        int updated = 0;
        foreach (var r in results)
        {
            if (catalogMap.TryGetValue(r.LotNumber, out var cl))
            {
                r.Size = cl.Size;
                r.HairLength = cl.HairLength;
                r.Clarity = cl.Clarity;
                if (r.BoxCount == 0) r.BoxCount = cl.BoxCount;
                if (r.TotalSkins == 0) r.TotalSkins = cl.TotalSkins;
                if (string.IsNullOrEmpty(r.SalesType)) r.SalesType = cl.SalesType;
                if (string.IsNullOrEmpty(r.Gender)) r.Gender = cl.Gender;
                if (string.IsNullOrEmpty(r.Group)) r.Group = cl.Group;
                if (string.IsNullOrEmpty(r.Color)) r.Color = cl.Color;
                if (string.IsNullOrEmpty(r.Quality)) r.Quality = cl.Quality;
                updated++;
            }
        }
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { total = results.Count, updated, notFound = results.Count - updated }, JsonOptions));
        return response;
    }

    [Function("UnsellInvoice")]
    public async Task<HttpResponseData> UnsellInvoice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "settlements/invoices/{invoiceId:int}/unsell")] HttpRequestData req, int invoiceId)
    {
        var invoice = await _db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // Get auction results linked to this invoice's lot numbers
        var lotNumbers = invoice.Lines.Select(l => l.LotNumber).ToList();
        var results = await _db.AuctionResults
            .Where(r => lotNumbers.Contains(r.LotNumber) && r.SoldToBuyerId == invoice.BuyerId)
            .ToListAsync();

        // Clear sold status on results
        foreach (var r in results)
        {
            r.SoldToBuyerId = null;
            r.SoldAt = null;
            r.CommissionType = null;
            r.CommissionValue = null;
            r.CommissionAmount = null;
        }

        // Delete invoice lines and invoice
        _db.InvoiceLines.RemoveRange(invoice.Lines);
        _db.Invoices.Remove(invoice);

        // Delete related sales history
        var history = await _db.LotSalesHistories
            .Where(h => h.InvoiceId == invoiceId)
            .ToListAsync();
        _db.LotSalesHistories.RemoveRange(history);

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { deleted = true, lotsUnsold = results.Count }, JsonOptions));
        return response;
    }

    private async Task<Dictionary<int, string>> GetLotShipmentStatusAsync(List<int> lotNumbers)
    {
        if (lotNumbers.Count == 0) return new();

        var lotShipmentMap = await _db.ShipmentLines
            .Where(sl => lotNumbers.Contains(sl.LotNumber))
            .Include(sl => sl.Shipment)
            .Select(sl => new { sl.LotNumber, sl.Shipment!.Status })
            .ToListAsync();

        return lotShipmentMap
            .GroupBy(x => x.LotNumber)
            .ToDictionary(g => g.Key, g => g.First().Status);
    }
}

public class SubmitAuctionResultRequest
{
    public int LotNumber { get; set; }
    public int BrokerId { get; set; }
    public decimal PriceEur { get; set; }
}

public class SellToBuyerRequest
{
    public List<int> AuctionResultIds { get; set; } = new();
    public int BuyerId { get; set; }
    public string? CommissionType { get; set; }
    public decimal? CommissionValue { get; set; }
    public string? Initials { get; set; }
}

public class TakebackRequestBody
{
    public List<int> AuctionResultIds { get; set; } = new();
    public string? Initials { get; set; }
}

public class TakebackResponseBody
{
    public bool Approve { get; set; }
}

public class ReassignUnsoldRequest
{
    public List<int> AuctionResultIds { get; set; } = new();
    public string Action { get; set; } = "";  // "internal" or "auction"
}
