using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

public class ShippingAddressFunctions
{
    private readonly AuctionDbContext _db;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ShippingAddressFunctions(AuctionDbContext db)
    {
        _db = db;
    }

    [Function("GetShippingAddresses")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipping-addresses")] HttpRequestData req)
    {
        var addresses = await _db.ShippingAddresses
            .Include(a => a.Buyer)
            .Where(a => a.IsActive)
            .OrderBy(a => a.Buyer!.Name)
            .ThenByDescending(a => a.IsDefault)
            .Select(a => new
            {
                a.Id,
                a.BuyerId,
                BuyerName = a.Buyer!.Name,
                BuyerNumber = a.Buyer.BuyerNumber,
                a.ContactName,
                a.AddressLine1,
                a.AddressLine2,
                a.Country,
                a.PostalCode,
                a.City,
                a.ContactPhone,
                a.MobilePhone,
                a.ContactEmail,
                a.IsDefault
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(addresses, JsonOptions));
        return response;
    }

    [Function("CreateShippingAddress")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shipping-addresses")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<ShippingAddressDto>();
        if (body == null || body.BuyerId == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var buyer = await _db.Buyers.FindAsync(body.BuyerId);
        if (buyer == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        if (body.IsDefault)
        {
            var existing = await _db.ShippingAddresses
                .Where(a => a.BuyerId == body.BuyerId && a.IsDefault)
                .ToListAsync();
            foreach (var a in existing) a.IsDefault = false;
        }

        var address = new ShippingAddress
        {
            BuyerId = body.BuyerId,
            ContactName = body.ContactName ?? "",
            AddressLine1 = body.AddressLine1 ?? "",
            AddressLine2 = body.AddressLine2 ?? "",
            Country = body.Country ?? "",
            PostalCode = body.PostalCode ?? "",
            City = body.City ?? "",
            ContactPhone = body.ContactPhone ?? "",
            MobilePhone = body.MobilePhone ?? "",
            ContactEmail = body.ContactEmail ?? "",
            IsDefault = body.IsDefault
        };

        _db.ShippingAddresses.Add(address);
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, id = address.Id }, JsonOptions));
        return response;
    }

    [Function("UpdateShippingAddress")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "shipping-addresses/{id:int}")] HttpRequestData req,
        int id)
    {
        var body = await req.ReadFromJsonAsync<ShippingAddressDto>();
        if (body == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var address = await _db.ShippingAddresses.FindAsync(id);
        if (address == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        if (body.IsDefault && !address.IsDefault)
        {
            var existing = await _db.ShippingAddresses
                .Where(a => a.BuyerId == address.BuyerId && a.IsDefault && a.Id != id)
                .ToListAsync();
            foreach (var a in existing) a.IsDefault = false;
        }

        address.ContactName = body.ContactName ?? "";
        address.AddressLine1 = body.AddressLine1 ?? "";
        address.AddressLine2 = body.AddressLine2 ?? "";
        address.Country = body.Country ?? "";
        address.PostalCode = body.PostalCode ?? "";
        address.City = body.City ?? "";
        address.ContactPhone = body.ContactPhone ?? "";
        address.MobilePhone = body.MobilePhone ?? "";
        address.ContactEmail = body.ContactEmail ?? "";
        address.IsDefault = body.IsDefault;

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true }, JsonOptions));
        return response;
    }

    [Function("DeleteShippingAddress")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "shipping-addresses/{id:int}")] HttpRequestData req,
        int id)
    {
        var address = await _db.ShippingAddresses.FindAsync(id);
        if (address == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        address.IsActive = false;
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true }, JsonOptions));
        return response;
    }
}

public class ShippingAddressDto
{
    public int BuyerId { get; set; }
    public string? ContactName { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? ContactPhone { get; set; }
    public string? MobilePhone { get; set; }
    public string? ContactEmail { get; set; }
    public bool IsDefault { get; set; }
}
