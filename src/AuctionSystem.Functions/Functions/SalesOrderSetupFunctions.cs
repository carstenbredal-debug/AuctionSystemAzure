using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class SalesOrderSetupFunctions
{
    private readonly CatalogDbContext _db;
    private readonly ILogger<SalesOrderSetupFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SalesOrderSetupFunctions(CatalogDbContext db, ILogger<SalesOrderSetupFunctions> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Lot Group Order ──────────────────────────────────────────

    [Function("GetLotGroupOrders")]
    public async Task<HttpResponseData> GetLotGroupOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sales-order-setup/lot-group-orders")] HttpRequestData req)
    {
        var items = await _db.LotGroupOrders.OrderBy(g => g.GroupOrder).ToListAsync();
        return await CreateJsonResponse(req, items);
    }

    [Function("CreateLotGroupOrder")]
    public async Task<HttpResponseData> CreateLotGroupOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sales-order-setup/lot-group-orders")] HttpRequestData req)
    {
        var dto = await req.ReadFromJsonAsync<LotGroupOrder>();
        if (dto == null || string.IsNullOrWhiteSpace(dto.ColumnName))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var existing = await _db.LotGroupOrders.FindAsync(dto.ColumnName);
        if (existing != null)
            return req.CreateResponse(System.Net.HttpStatusCode.Conflict);

        _db.LotGroupOrders.Add(dto);
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, dto, System.Net.HttpStatusCode.Created);
    }

    [Function("UpdateLotGroupOrder")]
    public async Task<HttpResponseData> UpdateLotGroupOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sales-order-setup/lot-group-orders/{columnName}")] HttpRequestData req, string columnName)
    {
        var dto = await req.ReadFromJsonAsync<LotGroupOrder>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var item = await _db.LotGroupOrders.FindAsync(columnName);
        if (item == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        item.GroupOrder = dto.GroupOrder;
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, item);
    }

    [Function("ReorderLotGroupOrders")]
    public async Task<HttpResponseData> ReorderLotGroupOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sales-order-setup/lot-group-orders/reorder")] HttpRequestData req)
    {
        var items = await req.ReadFromJsonAsync<List<LotGroupOrder>>();
        if (items == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        foreach (var dto in items)
        {
            var item = await _db.LotGroupOrders.FindAsync(dto.ColumnName);
            if (item != null) item.GroupOrder = dto.GroupOrder;
        }
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, await _db.LotGroupOrders.OrderBy(g => g.GroupOrder).ToListAsync());
    }

    [Function("DeleteLotGroupOrder")]
    public async Task<HttpResponseData> DeleteLotGroupOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sales-order-setup/lot-group-orders/{columnName}")] HttpRequestData req, string columnName)
    {
        var item = await _db.LotGroupOrders.FindAsync(columnName);
        if (item == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        _db.LotGroupOrders.Remove(item);
        await _db.SaveChangesAsync();
        return req.CreateResponse(System.Net.HttpStatusCode.NoContent);
    }

    // ── Lot Size Rule ────────────────────────────────────────────

    [Function("GetLotSizeRules")]
    public async Task<HttpResponseData> GetLotSizeRules(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sales-order-setup/lot-size-rules")] HttpRequestData req)
    {
        var items = await _db.LotSizeRules.OrderBy(r => r.Priority).ToListAsync();
        return await CreateJsonResponse(req, items);
    }

    [Function("CreateLotSizeRule")]
    public async Task<HttpResponseData> CreateLotSizeRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sales-order-setup/lot-size-rules")] HttpRequestData req)
    {
        var dto = await req.ReadFromJsonAsync<LotSizeRule>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        dto.RuleID = 0;
        _db.LotSizeRules.Add(dto);
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, dto, System.Net.HttpStatusCode.Created);
    }

    [Function("UpdateLotSizeRule")]
    public async Task<HttpResponseData> UpdateLotSizeRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sales-order-setup/lot-size-rules/{id:int}")] HttpRequestData req, int id)
    {
        var dto = await req.ReadFromJsonAsync<LotSizeRule>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var item = await _db.LotSizeRules.FindAsync(id);
        if (item == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        item.Gender = dto.Gender;
        item.Size = dto.Size;
        item.MaxBoxes = dto.MaxBoxes;
        item.MaxSkinsPerBox = dto.MaxSkinsPerBox;
        item.ShowlotSkins = dto.ShowlotSkins;
        item.MaxLotSizeExclShowlot = dto.MaxLotSizeExclShowlot;
        item.MaxLotSizeInclShowlot = dto.MaxLotSizeInclShowlot;
        item.Priority = dto.Priority;
        item.IsActive = dto.IsActive;
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, item);
    }

    [Function("DeleteLotSizeRule")]
    public async Task<HttpResponseData> DeleteLotSizeRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sales-order-setup/lot-size-rules/{id:int}")] HttpRequestData req, int id)
    {
        var item = await _db.LotSizeRules.FindAsync(id);
        if (item == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        _db.LotSizeRules.Remove(item);
        await _db.SaveChangesAsync();
        return req.CreateResponse(System.Net.HttpStatusCode.NoContent);
    }

    // ── Lot Sort Order ───────────────────────────────────────────

    [Function("GetLotSortOrders")]
    public async Task<HttpResponseData> GetLotSortOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sales-order-setup/lot-sort-orders")] HttpRequestData req)
    {
        var items = await _db.LotSortOrders.OrderBy(s => s.ColumnName).ThenBy(s => s.SortOrder).ToListAsync();
        return await CreateJsonResponse(req, items);
    }

    [Function("CreateLotSortOrder")]
    public async Task<HttpResponseData> CreateLotSortOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sales-order-setup/lot-sort-orders")] HttpRequestData req)
    {
        var dto = await req.ReadFromJsonAsync<LotSortOrder>();
        if (dto == null || string.IsNullOrWhiteSpace(dto.ColumnName) || string.IsNullOrWhiteSpace(dto.Value))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var existing = await _db.LotSortOrders.FindAsync(dto.ColumnName, dto.Value);
        if (existing != null)
            return req.CreateResponse(System.Net.HttpStatusCode.Conflict);

        _db.LotSortOrders.Add(dto);
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, dto, System.Net.HttpStatusCode.Created);
    }

    [Function("ReorderLotSortOrders")]
    public async Task<HttpResponseData> ReorderLotSortOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sales-order-setup/lot-sort-orders/reorder")] HttpRequestData req)
    {
        var items = await req.ReadFromJsonAsync<List<LotSortOrder>>();
        if (items == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        foreach (var dto in items)
        {
            var item = await _db.LotSortOrders.FindAsync(dto.ColumnName, dto.Value);
            if (item != null) item.SortOrder = dto.SortOrder;
        }
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, await _db.LotSortOrders.OrderBy(s => s.ColumnName).ThenBy(s => s.SortOrder).ToListAsync());
    }

    [Function("DeleteLotSortOrder")]
    public async Task<HttpResponseData> DeleteLotSortOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sales-order-setup/lot-sort-orders/{columnName}/{value}")] HttpRequestData req, string columnName, string value)
    {
        var item = await _db.LotSortOrders.FindAsync(columnName, value);
        if (item == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        _db.LotSortOrders.Remove(item);
        await _db.SaveChangesAsync();
        return req.CreateResponse(System.Net.HttpStatusCode.NoContent);
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
