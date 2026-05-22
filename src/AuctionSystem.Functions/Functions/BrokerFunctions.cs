using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class BrokerFunctions
{
    private readonly AuctionDbContext _db;
    private readonly ILogger<BrokerFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public BrokerFunctions(AuctionDbContext db, ILogger<BrokerFunctions> logger)
    {
        _db = db;
        _logger = logger;
    }

    [Function("GetBrokers")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "brokers")] HttpRequestData req)
    {
        var brokers = await _db.Brokers.Include(b => b.Buyers).ToListAsync();
        return await CreateJsonResponse(req, brokers);
    }

    [Function("GetBroker")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "brokers/{id:int}")] HttpRequestData req, int id)
    {
        var broker = await _db.Brokers.Include(b => b.Buyers).FirstOrDefaultAsync(b => b.Id == id);
        if (broker == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, broker);
    }

    [Function("CreateBroker")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "brokers")] HttpRequestData req)
    {
        var broker = await req.ReadFromJsonAsync<Broker>();
        if (broker == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        _db.Brokers.Add(broker);
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, broker, System.Net.HttpStatusCode.Created);
    }

    [Function("AddBuyerToBroker")]
    public async Task<HttpResponseData> AddBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "brokers/{brokerId:int}/buyers")] HttpRequestData req, int brokerId)
    {
        var buyer = await req.ReadFromJsonAsync<Buyer>();
        if (buyer == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        buyer.BrokerId = brokerId;
        _db.Buyers.Add(buyer);
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, buyer, System.Net.HttpStatusCode.Created);
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
