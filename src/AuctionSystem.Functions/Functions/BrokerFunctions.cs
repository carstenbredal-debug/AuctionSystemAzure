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

    [Function("UpdateBroker")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "brokers/{id:int}")] HttpRequestData req, int id)
    {
        var dto = await req.ReadFromJsonAsync<Broker>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        var broker = await _db.Brokers.FindAsync(id);
        if (broker == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        broker.BrokerNumber = dto.BrokerNumber;
        broker.CompanyName = dto.CompanyName;
        broker.CompanyName2 = dto.CompanyName2;
        broker.ErpAccountNumber = dto.ErpAccountNumber;
        broker.SearchName = dto.SearchName;
        broker.ContactPerson = dto.ContactPerson;
        broker.AddressLine1 = dto.AddressLine1;
        broker.AddressLine2 = dto.AddressLine2;
        broker.Country = dto.Country;
        broker.PostalCode = dto.PostalCode;
        broker.City = dto.City;
        broker.ContactPhone = dto.ContactPhone;
        broker.MobilePhone = dto.MobilePhone;
        broker.ContactEmail = dto.ContactEmail;
        broker.HomePage = dto.HomePage;
        broker.VatRegistrationNo = dto.VatRegistrationNo;
        broker.RegistrationNo = dto.RegistrationNo;
        broker.CustomerGroup = dto.CustomerGroup;
        broker.SalesPerson = dto.SalesPerson;
        broker.PaymentTerm = dto.PaymentTerm;
        broker.PaymentMethod = dto.PaymentMethod;
        broker.Currency = dto.Currency;
        broker.Language = dto.Language;
        broker.IsActive = dto.IsActive;
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, broker);
    }

    [Function("DeleteBroker")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "brokers/{id:int}")] HttpRequestData req, int id)
    {
        var broker = await _db.Brokers.FindAsync(id);
        if (broker == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        _db.Brokers.Remove(broker);
        await _db.SaveChangesAsync();
        return req.CreateResponse(System.Net.HttpStatusCode.NoContent);
    }

    [Function("GetBuyersByBroker")]
    public async Task<HttpResponseData> GetBuyers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "brokers/{brokerId:int}/buyers")] HttpRequestData req, int brokerId)
    {
        var buyers = await _db.Buyers.Where(b => b.BrokerId == brokerId).ToListAsync();
        return await CreateJsonResponse(req, buyers);
    }

    [Function("AddBuyerToBroker")]
    public async Task<HttpResponseData> AddBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "brokers/{brokerId:int}/buyers/add")] HttpRequestData req, int brokerId)
    {
        var buyer = await req.ReadFromJsonAsync<Buyer>();
        if (buyer == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        buyer.BrokerId = brokerId > 0 ? brokerId : null;
        _db.Buyers.Add(buyer);
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, buyer, System.Net.HttpStatusCode.Created);
    }

    [Function("GetAllBuyers")]
    public async Task<HttpResponseData> GetAllBuyers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "buyers")] HttpRequestData req)
    {
        var buyerIdsWithUsers = await _db.AppUsers
            .Where(u => u.BuyerId.HasValue && u.IsActive)
            .Select(u => u.BuyerId!.Value)
            .ToListAsync();

        var buyers = await _db.Buyers
            .Include(b => b.Broker)
            .Where(b => buyerIdsWithUsers.Contains(b.Id))
            .OrderBy(b => b.Name)
            .ToListAsync();

        return await CreateJsonResponse(req, buyers);
    }

    [Function("UpdateBuyer")]
    public async Task<HttpResponseData> UpdateBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "buyers/{id:int}")] HttpRequestData req, int id)
    {
        var dto = await req.ReadFromJsonAsync<Buyer>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        var buyer = await _db.Buyers.FindAsync(id);
        if (buyer == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        buyer.BuyerNumber = dto.BuyerNumber;
        buyer.Name = dto.Name;
        buyer.ContactEmail = dto.ContactEmail;
        buyer.ContactPhone = dto.ContactPhone;
        buyer.Address = dto.Address;
        buyer.BrokerId = dto.BrokerId;
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, buyer);
    }

    [Function("DeleteBuyer")]
    public async Task<HttpResponseData> DeleteBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "buyers/{id:int}")] HttpRequestData req, int id)
    {
        var buyer = await _db.Buyers.FindAsync(id);
        if (buyer == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        _db.Buyers.Remove(buyer);
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
