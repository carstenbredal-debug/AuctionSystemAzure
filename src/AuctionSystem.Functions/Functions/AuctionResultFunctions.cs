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
        r.CommissionAmount
    };

    [Function("GetAuctionResults")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results")] HttpRequestData req)
    {
        var results = await _db.AuctionResults
            .Include(r => r.Broker)
            .Include(r => r.SoldToBuyer)
            .OrderByDescending(r => r.ReceivedAt)
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(results.Select(ProjectResult), JsonOptions));
        return response;
    }

    [Function("GetAuctionResultsByBroker")]
    public async Task<HttpResponseData> GetByBroker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        var results = await _db.AuctionResults
            .Include(r => r.Broker)
            .Include(r => r.SoldToBuyer)
            .Where(r => r.BrokerId == brokerId)
            .OrderByDescending(r => r.ReceivedAt)
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(results.Select(ProjectResult), JsonOptions));
        return response;
    }

    [Function("SellLotsToBuyer")]
    public async Task<HttpResponseData> SellToBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results/sell-to-buyer")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SellToBuyerRequest>();
        if (body == null || body.AuctionResultIds == null || body.AuctionResultIds.Count == 0 || body.BuyerId <= 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

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

        foreach (var result in results)
        {
            result.SoldToBuyerId = body.BuyerId;
            result.SoldAt = DateTime.UtcNow;
            result.CommissionType = body.CommissionType;
            result.CommissionValue = body.CommissionValue;
            if (body.CommissionType == "percentage" && body.CommissionValue.HasValue)
            {
                var hammerPrice = result.TotalSkins * result.PriceEur;
                result.CommissionAmount = hammerPrice * body.CommissionValue.Value / 100m;
            }
            else if (body.CommissionType == "amount" && body.CommissionValue.HasValue)
            {
                result.CommissionAmount = body.CommissionValue.Value;
            }
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { soldCount = results.Count }, JsonOptions));
        return response;
    }

    [Function("GetAuctionResultsByBuyer")]
    public async Task<HttpResponseData> GetByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results/buyer/{buyerId:int}")] HttpRequestData req, int buyerId)
    {
        var results = await _db.AuctionResults
            .Include(r => r.Broker)
            .Include(r => r.SoldToBuyer)
            .Where(r => r.SoldToBuyerId == buyerId)
            .OrderByDescending(r => r.SoldAt)
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(results.Select(ProjectResult), JsonOptions));
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
                InitiatedBy = "Broker",
                Status = CustomerRequestStatus.Approved,
                RequestedAt = DateTime.UtcNow,
                RespondedAt = DateTime.UtcNow
            };
            _db.TakebackRequests.Add(takebackReq);
            created.Add(takebackReq);

            result.SoldToBuyerId = null;
            result.SoldAt = null;
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { requestCount = created.Count }, JsonOptions));
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

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { requestCount = created.Count }, JsonOptions));
        return response;
    }

    [Function("GetTakebackRequestsByBuyer")]
    public async Task<HttpResponseData> GetTakebacksByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "takeback-requests/buyer/{buyerId:int}")] HttpRequestData req, int buyerId)
    {
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

        if (takebackReq.Status != CustomerRequestStatus.Pending)
        {
            var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "Request already responded to" }, JsonOptions));
            return resp;
        }

        takebackReq.Status = body.Approve ? CustomerRequestStatus.Approved : CustomerRequestStatus.Declined;
        takebackReq.RespondedAt = DateTime.UtcNow;

        if (body.Approve)
        {
            takebackReq.AuctionResult.SoldToBuyerId = null;
            takebackReq.AuctionResult.SoldAt = null;
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { status = takebackReq.Status.ToString() }, JsonOptions));
        return response;
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
}

public class TakebackRequestBody
{
    public List<int> AuctionResultIds { get; set; } = new();
}

public class TakebackResponseBody
{
    public bool Approve { get; set; }
}
