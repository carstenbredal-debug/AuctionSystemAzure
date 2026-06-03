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

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
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
        try
        {
            var body = await req.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<LotGroupOrder>(body!, ReadOptions);
            if (dto == null || string.IsNullOrWhiteSpace(dto.ColumnName))
                return await CreateErrorResponse(req, "ColumnName is required.", System.Net.HttpStatusCode.BadRequest);

            var existing = await _db.LotGroupOrders.FindAsync(dto.ColumnName);
            if (existing != null)
                return await CreateErrorResponse(req, $"'{dto.ColumnName}' already exists.", System.Net.HttpStatusCode.Conflict);

            _db.LotGroupOrders.Add(dto);
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, dto, System.Net.HttpStatusCode.Created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating lot group order");
            return await CreateErrorResponse(req, ex.Message);
        }
    }

    [Function("UpdateLotGroupOrder")]
    public async Task<HttpResponseData> UpdateLotGroupOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sales-order-setup/lot-group-orders/update")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<LotGroupOrder>(body!, ReadOptions);
            if (dto == null || string.IsNullOrWhiteSpace(dto.ColumnName))
                return await CreateErrorResponse(req, "ColumnName is required.", System.Net.HttpStatusCode.BadRequest);

            var item = await _db.LotGroupOrders.FindAsync(dto.ColumnName);
            if (item == null)
                return await CreateErrorResponse(req, $"'{dto.ColumnName}' not found.", System.Net.HttpStatusCode.NotFound);

            item.GroupOrder = dto.GroupOrder;
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating lot group order");
            return await CreateErrorResponse(req, ex.Message);
        }
    }

    [Function("ReorderLotGroupOrders")]
    public async Task<HttpResponseData> ReorderLotGroupOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sales-order-setup/lot-group-orders/reorder")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var items = JsonSerializer.Deserialize<List<LotGroupOrder>>(body!, ReadOptions);
            if (items == null) return await CreateErrorResponse(req, "Invalid data.", System.Net.HttpStatusCode.BadRequest);

            foreach (var dto in items)
            {
                var item = await _db.LotGroupOrders.FindAsync(dto.ColumnName);
                if (item != null) item.GroupOrder = dto.GroupOrder;
            }
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, await _db.LotGroupOrders.OrderBy(g => g.GroupOrder).ToListAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reordering lot group orders");
            return await CreateErrorResponse(req, ex.Message);
        }
    }

    [Function("DeleteLotGroupOrder")]
    public async Task<HttpResponseData> DeleteLotGroupOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sales-order-setup/lot-group-orders/remove")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<LotGroupOrder>(body!, ReadOptions);
            if (dto == null || string.IsNullOrWhiteSpace(dto.ColumnName))
                return await CreateErrorResponse(req, "ColumnName is required.", System.Net.HttpStatusCode.BadRequest);

            var item = await _db.LotGroupOrders.FindAsync(dto.ColumnName);
            if (item == null)
                return await CreateErrorResponse(req, $"'{dto.ColumnName}' not found.", System.Net.HttpStatusCode.NotFound);

            _db.LotGroupOrders.Remove(item);
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting lot group order");
            return await CreateErrorResponse(req, ex.Message);
        }
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
        try
        {
            var body = await req.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<LotSizeRule>(body!, ReadOptions);
            if (dto == null) return await CreateErrorResponse(req, "Invalid data.", System.Net.HttpStatusCode.BadRequest);

            dto.RuleID = 0;
            _db.LotSizeRules.Add(dto);
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, dto, System.Net.HttpStatusCode.Created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating lot size rule");
            return await CreateErrorResponse(req, ex.Message);
        }
    }

    [Function("UpdateLotSizeRule")]
    public async Task<HttpResponseData> UpdateLotSizeRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sales-order-setup/lot-size-rules/update")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<LotSizeRule>(body!, ReadOptions);
            if (dto == null) return await CreateErrorResponse(req, "Invalid data.", System.Net.HttpStatusCode.BadRequest);

            var item = await _db.LotSizeRules.FindAsync(dto.RuleID);
            if (item == null)
                return await CreateErrorResponse(req, $"Rule {dto.RuleID} not found.", System.Net.HttpStatusCode.NotFound);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating lot size rule");
            return await CreateErrorResponse(req, ex.Message);
        }
    }

    [Function("DeleteLotSizeRule")]
    public async Task<HttpResponseData> DeleteLotSizeRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sales-order-setup/lot-size-rules/remove")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<LotSizeRule>(body!, ReadOptions);
            if (dto == null) return await CreateErrorResponse(req, "Invalid data.", System.Net.HttpStatusCode.BadRequest);

            var item = await _db.LotSizeRules.FindAsync(dto.RuleID);
            if (item == null)
                return await CreateErrorResponse(req, $"Rule {dto.RuleID} not found.", System.Net.HttpStatusCode.NotFound);

            _db.LotSizeRules.Remove(item);
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting lot size rule");
            return await CreateErrorResponse(req, ex.Message);
        }
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
        try
        {
            var body = await req.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<LotSortOrder>(body!, ReadOptions);
            if (dto == null || string.IsNullOrWhiteSpace(dto.ColumnName) || string.IsNullOrWhiteSpace(dto.Value))
                return await CreateErrorResponse(req, "ColumnName and Value are required.", System.Net.HttpStatusCode.BadRequest);

            var existing = await _db.LotSortOrders.FindAsync(dto.ColumnName, dto.Value);
            if (existing != null)
                return await CreateErrorResponse(req, $"'{dto.Value}' already exists in '{dto.ColumnName}'.", System.Net.HttpStatusCode.Conflict);

            _db.LotSortOrders.Add(dto);
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, dto, System.Net.HttpStatusCode.Created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating lot sort order");
            return await CreateErrorResponse(req, ex.Message);
        }
    }

    [Function("UpdateLotSortOrder")]
    public async Task<HttpResponseData> UpdateLotSortOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sales-order-setup/lot-sort-orders/update")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            _logger.LogInformation("UpdateLotSortOrder body: {Body}", body);
            var dto = JsonSerializer.Deserialize<LotSortOrder>(body!, ReadOptions);
            if (dto == null || string.IsNullOrWhiteSpace(dto.ColumnName) || string.IsNullOrWhiteSpace(dto.Value))
                return await CreateErrorResponse(req, "ColumnName and Value are required.", System.Net.HttpStatusCode.BadRequest);

            var item = await _db.LotSortOrders.FindAsync(dto.ColumnName, dto.Value);
            if (item == null)
                return await CreateErrorResponse(req, $"'{dto.Value}' not found in '{dto.ColumnName}'.", System.Net.HttpStatusCode.NotFound);

            var oldSortOrder = item.SortOrder;
            item.SortOrder = dto.SortOrder;
            var changes = await _db.SaveChangesAsync();
            _logger.LogInformation("UpdateLotSortOrder: {Col}/{Val} sortOrder {Old}->{New}, changes={Changes}",
                dto.ColumnName, dto.Value, oldSortOrder, dto.SortOrder, changes);
            return await CreateJsonResponse(req, new
            {
                item.ColumnName,
                item.Value,
                item.SortOrder,
                debug = new { rawBody = body, dtoSortOrder = dto.SortOrder, oldSortOrder, dbChanges = changes }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating lot sort order");
            return await CreateErrorResponse(req, ex.Message);
        }
    }

    [Function("ReorderLotSortOrders")]
    public async Task<HttpResponseData> ReorderLotSortOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sales-order-setup/lot-sort-orders/reorder")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var items = JsonSerializer.Deserialize<List<LotSortOrder>>(body!, ReadOptions);
            if (items == null) return await CreateErrorResponse(req, "Invalid data.", System.Net.HttpStatusCode.BadRequest);

            foreach (var dto in items)
            {
                var item = await _db.LotSortOrders.FindAsync(dto.ColumnName, dto.Value);
                if (item != null) item.SortOrder = dto.SortOrder;
            }
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, await _db.LotSortOrders.OrderBy(s => s.ColumnName).ThenBy(s => s.SortOrder).ToListAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reordering lot sort orders");
            return await CreateErrorResponse(req, ex.Message);
        }
    }

    [Function("DeleteLotSortOrder")]
    public async Task<HttpResponseData> DeleteLotSortOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sales-order-setup/lot-sort-orders/remove")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<LotSortOrder>(body!, ReadOptions);
            if (dto == null || string.IsNullOrWhiteSpace(dto.ColumnName) || string.IsNullOrWhiteSpace(dto.Value))
                return await CreateErrorResponse(req, "ColumnName and Value are required.", System.Net.HttpStatusCode.BadRequest);

            var item = await _db.LotSortOrders.FindAsync(dto.ColumnName, dto.Value);
            if (item == null)
                return await CreateErrorResponse(req, $"'{dto.Value}' not found in '{dto.ColumnName}'.", System.Net.HttpStatusCode.NotFound);

            _db.LotSortOrders.Remove(item);
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting lot sort order");
            return await CreateErrorResponse(req, ex.Message);
        }
    }

    // ── Catalog Number Rule ───────────────────────────────────────

    [Function("GetCatalogNumberRules")]
    public async Task<HttpResponseData> GetCatalogNumberRules(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sales-order-setup/catalog-number-rules")] HttpRequestData req)
    {
        var items = await _db.CatalogNumberRules.OrderBy(r => r.SalesType).ThenBy(r => r.Gender).ThenBy(r => r.Group).ToListAsync();
        return await CreateJsonResponse(req, items);
    }

    [Function("CreateCatalogNumberRule")]
    public async Task<HttpResponseData> CreateCatalogNumberRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sales-order-setup/catalog-number-rules")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<CatalogNumberRule>(body!, ReadOptions);
            if (dto == null) return await CreateErrorResponse(req, "Invalid data.", System.Net.HttpStatusCode.BadRequest);

            dto.CatalogNumberRuleID = 0;
            _db.CatalogNumberRules.Add(dto);
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, dto, System.Net.HttpStatusCode.Created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating catalog number rule");
            return await CreateErrorResponse(req, ex.Message);
        }
    }

    [Function("UpdateCatalogNumberRule")]
    public async Task<HttpResponseData> UpdateCatalogNumberRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sales-order-setup/catalog-number-rules/update")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<CatalogNumberRule>(body!, ReadOptions);
            if (dto == null) return await CreateErrorResponse(req, "Invalid data.", System.Net.HttpStatusCode.BadRequest);

            var item = await _db.CatalogNumberRules.FindAsync(dto.CatalogNumberRuleID);
            if (item == null)
                return await CreateErrorResponse(req, $"Rule {dto.CatalogNumberRuleID} not found.", System.Net.HttpStatusCode.NotFound);

            item.SalesType = dto.SalesType;
            item.Gender = dto.Gender;
            item.Group = dto.Group;
            item.StartNumber = dto.StartNumber;
            item.IsActive = dto.IsActive;
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating catalog number rule");
            return await CreateErrorResponse(req, ex.Message);
        }
    }

    [Function("DeleteCatalogNumberRule")]
    public async Task<HttpResponseData> DeleteCatalogNumberRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sales-order-setup/catalog-number-rules/remove")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var dto = JsonSerializer.Deserialize<CatalogNumberRule>(body!, ReadOptions);
            if (dto == null) return await CreateErrorResponse(req, "Invalid data.", System.Net.HttpStatusCode.BadRequest);

            var item = await _db.CatalogNumberRules.FindAsync(dto.CatalogNumberRuleID);
            if (item == null)
                return await CreateErrorResponse(req, $"Rule {dto.CatalogNumberRuleID} not found.", System.Net.HttpStatusCode.NotFound);

            _db.CatalogNumberRules.Remove(item);
            await _db.SaveChangesAsync();
            return await CreateJsonResponse(req, new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting catalog number rule");
            return await CreateErrorResponse(req, ex.Message);
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

    private static async Task<HttpResponseData> CreateErrorResponse(
        HttpRequestData req, string message, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.InternalServerError)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, JsonOptions));
        return response;
    }
}
