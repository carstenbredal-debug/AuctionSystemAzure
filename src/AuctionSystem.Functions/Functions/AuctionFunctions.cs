using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Domain.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class AuctionFunctions
{
    private readonly AuctionService _service;
    private readonly AuctionDbContext _db;
    private readonly ILogger<AuctionFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AuctionFunctions(AuctionService service, AuctionDbContext db, ILogger<AuctionFunctions> logger)
    {
        _service = service;
        _db = db;
        _logger = logger;
    }

    [Function("GetAuctions")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auctions")] HttpRequestData req)
    {
        var auctions = await _service.GetAllAuctionsAsync();
        return await CreateJsonResponse(req, auctions);
    }

    [Function("GetAuction")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auctions/{id:int}")] HttpRequestData req, int id)
    {
        var auction = await _service.GetAuctionAsync(id);
        if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, auction);
    }

    [Function("CreateAuction")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auctions")] HttpRequestData req)
    {
        var auction = await req.ReadFromJsonAsync<Auction>();
        if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        var created = await _service.CreateAuctionAsync(auction);
        return await CreateJsonResponse(req, created, System.Net.HttpStatusCode.Created);
    }

    [Function("UpdateAuctionStatus")]
    public async Task<HttpResponseData> UpdateStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "auctions/{id:int}/status")] HttpRequestData req, int id)
    {
        var status = await req.ReadFromJsonAsync<AuctionStatus>();
        var auction = await _service.UpdateStatusAsync(id, status);
        if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, auction);
    }

    [Function("GetLotsByAuction")]
    public async Task<HttpResponseData> GetLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auctions/{auctionId:int}/lots")] HttpRequestData req, int auctionId)
    {
        var lots = await _service.GetLotsByAuctionAsync(auctionId);
        var result = lots.Select(l => new
        {
            l.Id, l.LotNumber, l.Description, l.Category, l.Quantity, l.Unit,
            l.StartingPrice, l.HammerPrice, status = (int)l.Status, l.AuctionId
        });
        return await CreateJsonResponse(req, result);
    }

    [Function("AddLot")]
    public async Task<HttpResponseData> AddLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auctions/{auctionId:int}/lots")] HttpRequestData req, int auctionId)
    {
        var lot = await req.ReadFromJsonAsync<Lot>();
        if (lot == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        var created = await _service.AddLotAsync(auctionId, lot);
        return await CreateJsonResponse(req, created, System.Net.HttpStatusCode.Created);
    }

    [Function("GetLot")]
    public async Task<HttpResponseData> GetLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lots/{id:int}")] HttpRequestData req, int id)
    {
        var lot = await _service.GetLotAsync(id);
        if (lot == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, lot);
    }

    [Function("RecordHammerPrice")]
    public async Task<HttpResponseData> RecordHammer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "lots/{lotId:int}/hammer")] HttpRequestData req, int lotId)
    {
        var request = await req.ReadFromJsonAsync<HammerRequest>();
        if (request == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        var lot = await _service.RecordHammerPriceAsync(lotId, request.HammerPrice, request.WinningBrokerId);
        if (lot == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, lot);
    }

    [Function("SeedDatabase")]
    public async Task<HttpResponseData> Seed(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "seed")] HttpRequestData req)
    {
        SeedData.Initialize(_db);
        return await CreateJsonResponse(req, new { message = "Database seeded successfully" });
    }

    [Function("GetDashboardStats")]
    public async Task<HttpResponseData> GetStats(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard")] HttpRequestData req)
    {
        var recentAuctions = await _db.Auctions.OrderByDescending(a => a.ScheduledDate).Take(5).ToListAsync();
        var stats = new
        {
            totalAuctions = await _db.Auctions.CountAsync(),
            activeAuctions = await _db.Auctions.CountAsync(a => a.Status == AuctionStatus.Active),
            totalLots = await _db.Lots.CountAsync(),
            soldLots = await _db.Lots.CountAsync(l => l.Status == LotStatus.Sold),
            totalBrokers = await _db.Brokers.CountAsync(),
            totalSellers = await _db.Sellers.CountAsync(),
            totalBuyers = await _db.Buyers.CountAsync(),
            totalBids = await _db.Bids.CountAsync(),
            pendingInvoices = await _db.Invoices.CountAsync(i => i.Status == InvoiceStatus.Issued),
            pendingSettlements = await _db.Settlements.CountAsync(s => s.Status == SettlementStatus.Pending),
            recentAuctions
        };
        return await CreateJsonResponse(req, stats);
    }

    [Function("ResetAllData")]
    public async Task<HttpResponseData> ResetAllData(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "system/reset-all")] HttpRequestData req)
    {
        _logger.LogWarning("RESETTING ALL DATA (except SystemParameters)");

        var deleted = new Dictionary<string, int>();

        var ilCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.InvoiceLines");
        deleted["InvoiceLines"] = ilCount;
        var invCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.Invoices");
        deleted["Invoices"] = invCount;
        var tbCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.TakebackRequests");
        deleted["TakebackRequests"] = tbCount;
        var laCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.LotAllocations");
        deleted["LotAllocations"] = laCount;
        var arCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.AuctionResults");
        deleted["AuctionResults"] = arCount;
        var stCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.Settlements");
        deleted["Settlements"] = stCount;
        var bdCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.Bids");
        deleted["Bids"] = bdCount;
        var ltCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.Lots");
        deleted["Lots"] = ltCount;
        var auCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.Auctions");
        deleted["Auctions"] = auCount;
        var bbCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.BrokerBuyers");
        deleted["BrokerBuyers"] = bbCount;
        var crCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.BrokerCustomerRequests");
        deleted["BrokerCustomerRequests"] = crCount;
        var byCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.Buyers");
        deleted["Buyers"] = byCount;
        var brCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.Brokers");
        deleted["Brokers"] = brCount;
        var slCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.Sellers");
        deleted["Sellers"] = slCount;
        var usCount = await _db.Database.ExecuteSqlRawAsync("DELETE FROM auction.AppUsers");
        deleted["AppUsers"] = usCount;

        _logger.LogWarning("Reset complete: {@Deleted}", deleted);

        return await CreateJsonResponse(req, new { message = "All data reset (SystemParameters kept)", deleted });
    }

    private static async Task<HttpResponseData> CreateJsonResponse<T>(
        HttpRequestData req, T data, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(data, JsonOptions));
        return response;
    }
}

public record HammerRequest(decimal HammerPrice, int WinningBrokerId);
