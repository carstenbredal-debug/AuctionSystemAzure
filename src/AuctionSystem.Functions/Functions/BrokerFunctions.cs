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
        var brokers = await _db.Brokers.Include(b => b.BrokerBuyers).ToListAsync();
        brokers = brokers.OrderBy(b => int.TryParse(b.BrokerNumber, out var n) ? n : int.MaxValue).ToList();
        return await CreateJsonResponse(req, brokers);
    }

    [Function("GetBroker")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "brokers/{id:int}")] HttpRequestData req, int id)
    {
        var broker = await _db.Brokers.Include(b => b.BrokerBuyers).ThenInclude(bb => bb.Buyer).FirstOrDefaultAsync(b => b.Id == id);
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
        var buyers = await _db.BrokerBuyers
            .Where(bb => bb.BrokerId == brokerId)
            .Include(bb => bb.Buyer)
            .Select(bb => bb.Buyer)
            .ToListAsync();
        buyers = buyers.OrderBy(b => int.TryParse(b.BuyerNumber, out var n) ? n : int.MaxValue).ToList();
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
        var buyers = await _db.Buyers
            .Include(b => b.BrokerBuyers).ThenInclude(bb => bb.Broker)
            .ToListAsync();

        var result = buyers
            .OrderBy(b => int.TryParse(b.BuyerNumber, out var n) ? n : int.MaxValue)
            .Select(b => new
            {
                b.Id, b.BuyerNumber, b.Name, b.Name2, b.ErpAccountNumber, b.SearchName,
                b.ContactName, b.AddressLine1, b.AddressLine2, b.Country, b.PostalCode, b.City,
                b.ContactPhone, b.MobilePhone, b.ContactEmail, b.HomePage, b.VatRegistrationNo,
                b.RegistrationNo, b.CustomerGroup, b.SalesPerson, b.PaymentTerm, b.PaymentMethod,
                b.Currency, b.Language, b.BankName, b.BankAddress, b.BankIbanNumber, b.SwiftCode,
                b.BankCountry, b.Assignee, b.AssignmentOfReceivable, b.IsActive, b.Address,
                b.BrokerId, b.CreatedAt,
                Brokers = b.BrokerBuyers.Select(bb => new { bb.Broker.Id, bb.Broker.BrokerNumber, bb.Broker.CompanyName }).ToList()
            })
            .ToList();

        return await CreateJsonResponse(req, result);
    }

    [Function("GetBuyerById")]
    public async Task<HttpResponseData> GetBuyerById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "buyers/{id:int}")] HttpRequestData req, int id)
    {
        var buyer = await _db.Buyers.Include(b => b.BrokerBuyers).ThenInclude(bb => bb.Broker).FirstOrDefaultAsync(b => b.Id == id);
        if (buyer == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, buyer);
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
        buyer.Name2 = dto.Name2;
        buyer.ErpAccountNumber = dto.ErpAccountNumber;
        buyer.SearchName = dto.SearchName;
        buyer.ContactName = dto.ContactName;
        buyer.AddressLine1 = dto.AddressLine1;
        buyer.AddressLine2 = dto.AddressLine2;
        buyer.Country = dto.Country;
        buyer.PostalCode = dto.PostalCode;
        buyer.City = dto.City;
        buyer.ContactPhone = dto.ContactPhone;
        buyer.MobilePhone = dto.MobilePhone;
        buyer.ContactEmail = dto.ContactEmail;
        buyer.HomePage = dto.HomePage;
        buyer.VatRegistrationNo = dto.VatRegistrationNo;
        buyer.RegistrationNo = dto.RegistrationNo;
        buyer.CustomerGroup = dto.CustomerGroup;
        buyer.SalesPerson = dto.SalesPerson;
        buyer.PaymentTerm = dto.PaymentTerm;
        buyer.PaymentMethod = dto.PaymentMethod;
        buyer.Currency = dto.Currency;
        buyer.Language = dto.Language;
        buyer.BankName = dto.BankName;
        buyer.BankAddress = dto.BankAddress;
        buyer.BankIbanNumber = dto.BankIbanNumber;
        buyer.SwiftCode = dto.SwiftCode;
        buyer.BankCountry = dto.BankCountry;
        buyer.Assignee = dto.Assignee;
        buyer.AssignmentOfReceivable = dto.AssignmentOfReceivable;
        buyer.IsActive = dto.IsActive;
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
