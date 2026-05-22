using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class SettlementFunctions
{
    private readonly SettlementService _service;
    private readonly ILogger<SettlementFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SettlementFunctions(SettlementService service, ILogger<SettlementFunctions> logger)
    {
        _service = service;
        _logger = logger;
    }

    [Function("GenerateInvoice")]
    public async Task<HttpResponseData> GenerateInvoice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settlements/invoices/generate")] HttpRequestData req)
    {
        var request = await req.ReadFromJsonAsync<GenerateInvoiceRequest>();
        if (request == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var invoice = await _service.GenerateInvoiceAsync(request.AuctionId, request.BrokerId);
        if (invoice == null)
        {
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await errorResponse.WriteStringAsync("No won lots for this broker in this auction");
            return errorResponse;
        }
        return await CreateJsonResponse(req, invoice);
    }

    [Function("GetInvoicesByBroker")]
    public async Task<HttpResponseData> GetInvoicesByBroker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoices/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        var invoices = await _service.GetInvoicesByBrokerAsync(brokerId);
        return await CreateJsonResponse(req, invoices);
    }

    [Function("MarkInvoicePaid")]
    public async Task<HttpResponseData> MarkPaid(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settlements/invoices/{invoiceId:int}/paid")] HttpRequestData req, int invoiceId)
    {
        var invoice = await _service.MarkInvoicePaidAsync(invoiceId);
        if (invoice == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, invoice);
    }

    [Function("CreateSettlement")]
    public async Task<HttpResponseData> CreateSettlement(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settlements/create")] HttpRequestData req)
    {
        var lotId = await req.ReadFromJsonAsync<int>();
        var settlement = await _service.CreateSettlementAsync(lotId);
        if (settlement == null)
        {
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await errorResponse.WriteStringAsync("Cannot create settlement for this lot");
            return errorResponse;
        }
        return await CreateJsonResponse(req, settlement);
    }

    [Function("GetSettlementsBySeller")]
    public async Task<HttpResponseData> GetBySeller(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/seller/{sellerId:int}")] HttpRequestData req, int sellerId)
    {
        var settlements = await _service.GetSettlementsBySellerAsync(sellerId);
        return await CreateJsonResponse(req, settlements);
    }

    [Function("MarkSettlementComplete")]
    public async Task<HttpResponseData> MarkComplete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settlements/{settlementId:int}/complete")] HttpRequestData req, int settlementId)
    {
        var settlement = await _service.MarkSettlementCompletedAsync(settlementId);
        if (settlement == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, settlement);
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

public record GenerateInvoiceRequest(int AuctionId, int BrokerId);
