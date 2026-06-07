using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

public class CatalogLotFunctions
{
    private readonly CatalogDbContext _catalogDb;
    private readonly AuctionDbContext _auctionDb;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CatalogLotFunctions(CatalogDbContext catalogDb, AuctionDbContext auctionDb)
    {
        _catalogDb = catalogDb;
        _auctionDb = auctionDb;
    }

    [Function("GetCatalogLots")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog-lots")] HttpRequestData req)
    {
        var lots = await _catalogDb.CatalogLots
            .OrderBy(l => l.CatalogSortOrder)
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(lots, JsonOptions));
        return response;
    }

    [Function("GetCatalogLot")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog-lots/{id:int}")] HttpRequestData req, int id)
    {
        var lot = await _catalogDb.CatalogLots.FindAsync(id);
        if (lot == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(lot, JsonOptions));
        return response;
    }

    [Function("GetDbSchema")]
    public async Task<HttpResponseData> GetDbSchema(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diag/schema/{tableName}")] HttpRequestData req, string tableName)
    {
        var columns = await _catalogDb.Database
            .SqlQueryRaw<SchemaColumn>(
                "SELECT COLUMN_NAME as ColumnName, DATA_TYPE as DataType, CHARACTER_MAXIMUM_LENGTH as MaxLength FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = {0} ORDER BY ORDINAL_POSITION",
                tableName)
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { table = tableName, columns }, JsonOptions));
        return response;
    }

    [Function("ImportCatalogLotsToAuction")]
    public async Task<HttpResponseData> ImportToAuction(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auctions/{auctionId:int}/import-catalog-lots")] HttpRequestData req, int auctionId)
    {
        try
        {
            var auction = await _auctionDb.Auctions.FindAsync(auctionId);
            if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

            var body = await req.ReadFromJsonAsync<ImportCatalogLotsRequest>();
            if (body == null || body.CatalogLotIds == null || body.CatalogLotIds.Count == 0)
                return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

            var catalogLots = await _catalogDb.CatalogLots
                .Where(c => body.CatalogLotIds.Contains(c.CatalogLotID))
                .ToListAsync();

            var existingLotNumbers = await _auctionDb.Lots
                .Where(l => l.AuctionId == auctionId)
                .Select(l => l.LotNumber)
                .ToListAsync();

            var imported = new List<object>();
            foreach (var cl in catalogLots)
            {
                if (existingLotNumbers.Contains(cl.LotNumber))
                    continue;

                var lot = new Domain.Entities.Lot
                {
                    AuctionId = auctionId,
                    LotNumber = cl.LotNumber,
                    Description = $"{cl.SalesType} {cl.Gender} {cl.Color} {cl.Quality}".Trim(),
                    Category = cl.Group,
                    Quantity = cl.TotalSkins,
                    Unit = "skins",
                    StartingPrice = 0,
                    Status = Domain.Enums.LotStatus.Pending,
                    FarmerId = body.FarmerId > 0 ? body.FarmerId : null
                };
                _auctionDb.Lots.Add(lot);
                imported.Add(new { cl.CatalogLotID, cl.LotNumber, lot.Description, lot.Quantity });
            }

            await _auctionDb.SaveChangesAsync();

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { importedCount = imported.Count, lots = imported }, JsonOptions));
            return response;
        }
        catch (Exception ex)
        {
            var response = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { error = ex.Message, inner = ex.InnerException?.Message, stack = ex.StackTrace }, JsonOptions));
            return response;
        }
    }
}

public class ImportCatalogLotsRequest
{
    public List<int> CatalogLotIds { get; set; } = new();
    public int FarmerId { get; set; }
}

public class SchemaColumn
{
    public string ColumnName { get; set; } = "";
    public string DataType { get; set; } = "";
    public int? MaxLength { get; set; }
}
