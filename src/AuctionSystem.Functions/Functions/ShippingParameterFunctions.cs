using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

public class ShippingParameterFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ShippingParameterFunctions(AuctionDbContext db, CatalogDbContext catalogDb)
    {
        _db = db;
        _catalogDb = catalogDb;
    }

    [Function("GetBoxTypeDimensions")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipping-parameters/box-types")] HttpRequestData req)
    {
        var distinctBoxTypes = await _catalogDb.Database
            .SqlQueryRaw<BoxTypeResult>("SELECT DISTINCT BoxType FROM auction.boxes WHERE BoxType IS NOT NULL AND BoxType <> ''")
            .ToListAsync();

        var saved = await _db.BoxTypeDimensions.ToListAsync();
        var savedMap = saved.ToDictionary(d => d.BoxType, d => d);

        var result = distinctBoxTypes
            .Select(bt =>
            {
                savedMap.TryGetValue(bt.BoxType, out var dim);
                return new
                {
                    boxType = bt.BoxType,
                    heightM = dim?.HeightM ?? 0m,
                    widthM = dim?.WidthM ?? 0m,
                    lengthM = dim?.LengthM ?? 0m,
                    weightKg = dim?.WeightKg ?? 0m,
                    id = dim?.Id,
                    updatedAt = dim?.UpdatedAt
                };
            })
            .OrderBy(x => x.boxType)
            .ToList();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, JsonOptions));
        return response;
    }

    [Function("SaveBoxTypeDimension")]
    public async Task<HttpResponseData> Save(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shipping-parameters/box-types")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SaveBoxTypeDimensionRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.BoxType))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var existing = await _db.BoxTypeDimensions.FirstOrDefaultAsync(d => d.BoxType == body.BoxType);
        if (existing != null)
        {
            existing.HeightM = body.HeightM;
            existing.WidthM = body.WidthM;
            existing.LengthM = body.LengthM;
            existing.WeightKg = body.WeightKg;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.BoxTypeDimensions.Add(new BoxTypeDimension
            {
                BoxType = body.BoxType,
                HeightM = body.HeightM,
                WidthM = body.WidthM,
                LengthM = body.LengthM,
                WeightKg = body.WeightKg,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, boxType = body.BoxType }, JsonOptions));
        return response;
    }

    [Function("SaveAllBoxTypeDimensions")]
    public async Task<HttpResponseData> SaveAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shipping-parameters/box-types/bulk")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<List<SaveBoxTypeDimensionRequest>>();
        if (body == null || body.Count == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var existing = await _db.BoxTypeDimensions.ToListAsync();
        var map = existing.ToDictionary(d => d.BoxType, d => d);

        foreach (var item in body)
        {
            if (string.IsNullOrWhiteSpace(item.BoxType)) continue;
            if (map.TryGetValue(item.BoxType, out var dim))
            {
                dim.HeightM = item.HeightM;
                dim.WidthM = item.WidthM;
                dim.LengthM = item.LengthM;
                dim.WeightKg = item.WeightKg;
                dim.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.BoxTypeDimensions.Add(new BoxTypeDimension
                {
                    BoxType = item.BoxType,
                    HeightM = item.HeightM,
                    WidthM = item.WidthM,
                    LengthM = item.LengthM,
                    WeightKg = item.WeightKg,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, updated = body.Count }, JsonOptions));
        return response;
    }
}

public class BoxTypeResult
{
    public string BoxType { get; set; } = "";
}

public class SaveBoxTypeDimensionRequest
{
    public string BoxType { get; set; } = "";
    public decimal HeightM { get; set; }
    public decimal WidthM { get; set; }
    public decimal LengthM { get; set; }
    public decimal WeightKg { get; set; }
}
