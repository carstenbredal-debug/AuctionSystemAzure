using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Enums;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

public class AuctionTransactionFunctions
{
    private readonly AuctionDbContext _db;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public AuctionTransactionFunctions(AuctionDbContext db)
    {
        _db = db;
    }

    [Function("GetAuctionTransactions")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-transactions")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var auctionIdStr = query["auctionId"];

        var q = _db.AuctionTransactions
            .Include(t => t.Broker)
            .Include(t => t.Buyer)
            .AsQueryable();

        if (int.TryParse(auctionIdStr, out var auctionId))
            q = q.Where(t => t.AuctionId == auctionId);

        var transactions = await q
            .OrderBy(t => _db.Lots.Where(l => l.AuctionId == t.AuctionId && l.LotNumber == t.LotNumber)
                                  .Select(l => l.CatalogSortOrder).FirstOrDefault())
            .ThenBy(t => t.LotNumber)
            .ThenBy(t => t.TransactionType)
            .Select(t => new
            {
                t.Id,
                t.AuctionId,
                t.LotNumber,
                transactionType = t.TransactionType.ToString(),
                t.BrokerId,
                brokerNumber = t.Broker.BrokerNumber,
                brokerName = t.Broker.CompanyName,
                t.BuyerId,
                buyerNumber = t.Buyer != null ? t.Buyer.BuyerNumber : null,
                buyerName = t.Buyer != null ? t.Buyer.Name : null,
                t.Description,
                t.Quantity,
                t.UnitPrice,
                t.Amount,
                t.DebitAccount,
                t.CreditAccount,
                t.CreatedAt,
                t.AuctionResultId
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(transactions, JsonOptions));
        return response;
    }

    [Function("GetAuctionTransactionSummary")]
    public async Task<HttpResponseData> GetSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-transactions/summary")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var auctionIdStr = query["auctionId"];

        var q = _db.AuctionTransactions.AsQueryable();
        if (int.TryParse(auctionIdStr, out var auctionId))
            q = q.Where(t => t.AuctionId == auctionId);

        var summary = await q
            .GroupBy(t => t.TransactionType)
            .Select(g => new
            {
                transactionType = g.Key.ToString(),
                count = g.Count(),
                totalAmount = g.Sum(t => t.Amount)
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(summary, JsonOptions));
        return response;
    }
}
