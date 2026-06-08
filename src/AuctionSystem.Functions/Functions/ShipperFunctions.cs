using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

public class ShipperFunctions
{
    private readonly AuctionDbContext _db;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ShipperFunctions(AuctionDbContext db)
    {
        _db = db;
    }

    [Function("GetShippers")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shippers")] HttpRequestData req)
    {
        var shippers = await _db.Shippers
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Code,
                s.ContactName,
                s.Phone,
                s.Email,
                s.AddressLine1,
                s.AddressLine2,
                s.City,
                s.PostalCode,
                s.Country,
                s.Website,
                s.TrackingUrlTemplate,
                s.IsActive
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(shippers, JsonOptions));
        return response;
    }

    [Function("CreateShipper")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shippers")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<ShipperDto>();
        if (body == null || string.IsNullOrWhiteSpace(body.Name))
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            await bad.WriteStringAsync(JsonSerializer.Serialize(new { error = "Name is required" }, JsonOptions));
            return bad;
        }

        if (!string.IsNullOrWhiteSpace(body.Code))
        {
            var existing = await _db.Shippers.AnyAsync(s => s.Code == body.Code && s.IsActive);
            if (existing)
            {
                var conflict = req.CreateResponse(System.Net.HttpStatusCode.Conflict);
                conflict.Headers.Add("Content-Type", "application/json");
                await conflict.WriteStringAsync(JsonSerializer.Serialize(new { error = $"Shipper with code '{body.Code}' already exists" }, JsonOptions));
                return conflict;
            }
        }

        var shipper = new Shipper
        {
            Name = body.Name,
            Code = body.Code ?? "",
            ContactName = body.ContactName ?? "",
            Phone = body.Phone ?? "",
            Email = body.Email ?? "",
            AddressLine1 = body.AddressLine1 ?? "",
            AddressLine2 = body.AddressLine2 ?? "",
            City = body.City ?? "",
            PostalCode = body.PostalCode ?? "",
            Country = body.Country ?? "",
            Website = body.Website ?? "",
            TrackingUrlTemplate = body.TrackingUrlTemplate ?? ""
        };

        _db.Shippers.Add(shipper);
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, id = shipper.Id }, JsonOptions));
        return response;
    }

    [Function("UpdateShipper")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "shippers/{id:int}")] HttpRequestData req,
        int id)
    {
        var body = await req.ReadFromJsonAsync<ShipperDto>();
        if (body == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var shipper = await _db.Shippers.FindAsync(id);
        if (shipper == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        if (!string.IsNullOrWhiteSpace(body.Code) && body.Code != shipper.Code)
        {
            var existing = await _db.Shippers.AnyAsync(s => s.Code == body.Code && s.IsActive && s.Id != id);
            if (existing)
            {
                var conflict = req.CreateResponse(System.Net.HttpStatusCode.Conflict);
                conflict.Headers.Add("Content-Type", "application/json");
                await conflict.WriteStringAsync(JsonSerializer.Serialize(new { error = $"Shipper with code '{body.Code}' already exists" }, JsonOptions));
                return conflict;
            }
        }

        shipper.Name = body.Name ?? shipper.Name;
        shipper.Code = body.Code ?? shipper.Code;
        shipper.ContactName = body.ContactName ?? "";
        shipper.Phone = body.Phone ?? "";
        shipper.Email = body.Email ?? "";
        shipper.AddressLine1 = body.AddressLine1 ?? "";
        shipper.AddressLine2 = body.AddressLine2 ?? "";
        shipper.City = body.City ?? "";
        shipper.PostalCode = body.PostalCode ?? "";
        shipper.Country = body.Country ?? "";
        shipper.Website = body.Website ?? "";
        shipper.TrackingUrlTemplate = body.TrackingUrlTemplate ?? "";

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true }, JsonOptions));
        return response;
    }

    [Function("DeleteShipper")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "shippers/{id:int}")] HttpRequestData req,
        int id)
    {
        var shipper = await _db.Shippers.FindAsync(id);
        if (shipper == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        shipper.IsActive = false;
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true }, JsonOptions));
        return response;
    }
}

public class ShipperDto
{
    public string? Name { get; set; }
    public string? Code { get; set; }
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Website { get; set; }
    public string? TrackingUrlTemplate { get; set; }
}
