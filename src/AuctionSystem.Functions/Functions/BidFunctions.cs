using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class BidFunctions
{
    private readonly BidService _bidService;
    private readonly ILogger<BidFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public BidFunctions(BidService bidService, ILogger<BidFunctions> logger)
    {
        _bidService = bidService;
        _logger = logger;
    }

    [Function("PlaceBid")]
    public async Task<HttpResponseData> PlaceBid(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bids")] HttpRequestData req)
    {
        var request = await req.ReadFromJsonAsync<PlaceBidRequest>();
        if (request == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var bid = await _bidService.PlaceBidAsync(request.LotId, request.BrokerId, request.Amount);
        if (bid == null)
        {
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await errorResponse.WriteStringAsync("Cannot place bid on this lot");
            return errorResponse;
        }
        return await CreateJsonResponse(req, bid);
    }

    [Function("GetBidsByBroker")]
    public async Task<HttpResponseData> GetByBroker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bids/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        var bids = await _bidService.GetBidsByBrokerAsync(brokerId);
        return await CreateJsonResponse(req, bids);
    }

    [Function("AllocateLot")]
    public async Task<HttpResponseData> Allocate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bids/allocate")] HttpRequestData req)
    {
        var request = await req.ReadFromJsonAsync<AllocateRequest>();
        if (request == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var allocation = await _bidService.AllocateLotAsync(
            request.LotId, request.BrokerId, request.BuyerId, request.Quantity, request.PricePerUnit);
        if (allocation == null)
        {
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await errorResponse.WriteStringAsync("Cannot allocate this lot");
            return errorResponse;
        }
        return await CreateJsonResponse(req, allocation);
    }

    [Function("GetAllocationsByBroker")]
    public async Task<HttpResponseData> GetAllocationsByBroker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bids/allocations/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        var allocations = await _bidService.GetAllocationsByBrokerAsync(brokerId);
        return await CreateJsonResponse(req, allocations);
    }

    [Function("GetAllocationsByBuyer")]
    public async Task<HttpResponseData> GetAllocationsByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bids/allocations/buyer/{buyerId:int}")] HttpRequestData req, int buyerId)
    {
        var allocations = await _bidService.GetAllocationsByBuyerAsync(buyerId);
        return await CreateJsonResponse(req, allocations);
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

public record PlaceBidRequest(int LotId, int BrokerId, decimal Amount);
public record AllocateRequest(int LotId, int BrokerId, int BuyerId, int Quantity, decimal PricePerUnit);
