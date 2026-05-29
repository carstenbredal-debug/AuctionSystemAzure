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

        try
        {
            var tables = new[] { "InvoiceLines", "Invoices", "TakebackRequests", "LotAllocations",
                "AuctionResults", "Settlements", "Bids", "Lots", "Auctions",
                "BrokerBuyers", "BrokerCustomerRequests", "Buyers", "Brokers", "Sellers", "AppUsers" };

            // Disable FK constraints
            foreach (var t in tables)
                await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE auction.[{t}] NOCHECK CONSTRAINT ALL");

            // Delete all data
            foreach (var t in tables)
                await _db.Database.ExecuteSqlRawAsync($"DELETE FROM auction.[{t}]");

            // Re-enable FK constraints
            foreach (var t in tables)
                await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE auction.[{t}] WITH CHECK CHECK CONSTRAINT ALL");

            // Get remaining counts to confirm
            var brokerCount = await _db.Brokers.CountAsync();
            var buyerCount = await _db.Buyers.CountAsync();
            var sellerCount = await _db.Sellers.CountAsync();
            var auctionCount = await _db.Auctions.CountAsync();
            var paramCount = await _db.SystemParameters.CountAsync();

            _logger.LogWarning("Reset complete");

            return await CreateJsonResponse(req, new
            {
                message = "All data reset (SystemParameters kept)",
                remaining = new
                {
                    brokers = brokerCount,
                    buyers = buyerCount,
                    sellers = sellerCount,
                    auctions = auctionCount,
                    systemParameters = paramCount
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset data");
            return await CreateJsonResponse(req, new { error = ex.Message }, System.Net.HttpStatusCode.InternalServerError);
        }
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
