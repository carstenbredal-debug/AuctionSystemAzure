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

public class AuctionResultFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly ILogger<AuctionResultFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AuctionResultFunctions(AuctionDbContext db, CatalogDbContext catalogDb, ILogger<AuctionResultFunctions> logger)
    {
        _db = db;
        _catalogDb = catalogDb;
        _logger = logger;
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

        var catalogLot = await _catalogDb.CatalogLots
            .FirstOrDefaultAsync(c => c.LotNumber == body.LotNumber);

        var result = new AuctionResult
        {
            LotNumber = body.LotNumber,
            BrokerId = body.BrokerId,
            PriceEur = body.PriceEur,
            SalesType = catalogLot?.SalesType,
            Gender = catalogLot?.Gender,
            Group = catalogLot?.Group,
            Color = catalogLot?.Color,
            Quality = catalogLot?.Quality,
            Size = catalogLot?.Size,
            Clarity = catalogLot?.Clarity,
            HairLength = catalogLot?.HairLength,
            TotalSkins = catalogLot?.TotalSkins ?? 0,
            BoxCount = catalogLot?.BoxCount ?? 0,
            Processed = false,
            ReceivedAt = DateTime.UtcNow
        };

        _db.AuctionResults.Add(result);
        await _db.SaveChangesAsync();

        await ProcessAuctionResult(result, catalogLot);

        var resp = req.CreateResponse(System.Net.HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/json");
        await resp.WriteStringAsync(JsonSerializer.Serialize(new
        {
            resultId = result.Id,
            lotNumber = result.LotNumber,
            brokerId = result.BrokerId,
            priceEur = result.PriceEur,
            processed = result.Processed,
            catalogLotFound = catalogLot != null,
            salesType = result.SalesType,
            gender = result.Gender,
            group = result.Group,
            color = result.Color,
            quality = result.Quality,
            totalSkins = result.TotalSkins
        }, JsonOptions));
        return resp;
    }

    private async Task ProcessAuctionResult(AuctionResult result, CatalogLot? catalogLot)
    {
        try
        {
            var activeAuction = await _db.Auctions
                .Where(a => a.Status == AuctionStatus.InProgress || a.Status == AuctionStatus.Scheduled)
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

            var existingLot = await _db.Lots
                .FirstOrDefaultAsync(l => l.LotNumber == result.LotNumber && l.AuctionId == activeAuction.Id);

            if (existingLot == null)
            {
                var defaultSeller = await _db.Sellers.FirstOrDefaultAsync();
                if (defaultSeller == null)
                {
                    _logger.LogWarning("No seller found to assign lot {LotNumber}", result.LotNumber);
                    return;
                }

                existingLot = new Lot
                {
                    AuctionId = activeAuction.Id,
                    LotNumber = result.LotNumber,
                    Description = catalogLot != null
                        ? $"{catalogLot.SalesType} {catalogLot.Gender} {catalogLot.Color} {catalogLot.Quality}".Trim()
                        : $"Lot {result.LotNumber}",
                    Category = catalogLot?.Group,
                    Quantity = catalogLot?.TotalSkins ?? 1,
                    Unit = "skins",
                    StartingPrice = 0,
                    HammerPrice = result.PriceEur,
                    Status = LotStatus.Sold,
                    SellerId = defaultSeller.Id
                };
                _db.Lots.Add(existingLot);
                await _db.SaveChangesAsync();
            }
            else
            {
                existingLot.HammerPrice = result.PriceEur;
                existingLot.Status = LotStatus.Sold;
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

    [Function("GetAuctionResults")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results")] HttpRequestData req)
    {
        var results = await _db.AuctionResults
            .Include(r => r.Broker)
            .OrderByDescending(r => r.ReceivedAt)
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(results.Select(r => new
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
            r.ProcessedAt
        }), JsonOptions));
        return response;
    }

    [Function("GetAuctionResultsByBroker")]
    public async Task<HttpResponseData> GetByBroker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        var results = await _db.AuctionResults
            .Include(r => r.Broker)
            .Where(r => r.BrokerId == brokerId)
            .OrderByDescending(r => r.ReceivedAt)
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(results.Select(r => new
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
            r.ProcessedAt
        }), JsonOptions));
        return response;
    }
}

public class SubmitAuctionResultRequest
{
    public int LotNumber { get; set; }
    public int BrokerId { get; set; }
    public decimal PriceEur { get; set; }
}
