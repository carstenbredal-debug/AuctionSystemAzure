using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Functions.Auth;
using AuctionSystem.Functions.BusinessCentral.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class BrokerFunctions
{
    private readonly AuctionDbContext _db;
    private readonly ILogger<BrokerFunctions> _logger;
    private readonly BusinessCentralSyncService? _bcSync;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public BrokerFunctions(AuctionDbContext db, ILogger<BrokerFunctions> logger, BusinessCentralSyncService? bcSync = null)
    {
        _db = db;
        _logger = logger;
        _bcSync = bcSync;
    }

    [Function("GetBrokers")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "brokers")] HttpRequestData req)
    {
        // Non-admins (typists, buyers, brokers using the broker dropdowns) get the same shape but
        // with sensitive financial/banking/tax fields redacted; admins get full detail.
        var isAdmin = req.FunctionContext.IsAdmin();
        var brokers = await _db.Brokers
            .Select(b => new
            {
                b.Id, b.BrokerNumber, b.CompanyName, b.CompanyName2,
                b.SearchName, b.ContactPerson, b.AddressLine1, b.AddressLine2,
                b.Country, b.PostalCode, b.City, b.ContactPhone, b.MobilePhone,
                b.ContactEmail, b.HomePage,
                VatRegistrationNo = isAdmin ? b.VatRegistrationNo : "",
                RegistrationNo = isAdmin ? b.RegistrationNo : "",
                b.CustomerGroup, b.SalesPerson,
                PaymentTerm = isAdmin ? b.PaymentTerm : "",
                PaymentMethod = isAdmin ? b.PaymentMethod : "",
                b.Currency, b.Language,
                GenBusPostingGroup = isAdmin ? b.GenBusPostingGroup : "",
                VatBusPostingGroup = isAdmin ? b.VatBusPostingGroup : "",
                CustomerPostingGroup = isAdmin ? b.CustomerPostingGroup : "",
                CreditLimit = isAdmin ? b.CreditLimit : 0m,
                Blocked = isAdmin ? b.Blocked : "",
                b.IsActive, b.Address, b.CreatedAt,
                BuyerCount = b.BrokerBuyers.Count
            })
            .ToListAsync();
        var sorted = brokers.OrderBy(b => int.TryParse(b.BrokerNumber, out var n) ? n : int.MaxValue).ToList();
        return await CreateJsonResponse(req, sorted);
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
        await TryPushBrokerToBcAsync(broker);
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
        broker.GenBusPostingGroup = dto.GenBusPostingGroup;
        broker.VatBusPostingGroup = dto.VatBusPostingGroup;
        broker.CustomerPostingGroup = dto.CustomerPostingGroup;
        broker.CreditLimit = dto.CreditLimit;
        broker.Blocked = dto.Blocked;
        broker.IsActive = dto.IsActive;
        await _db.SaveChangesAsync();
        await TryPushBrokerToBcAsync(broker);
        return await CreateJsonResponse(req, broker);
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
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
        if (!req.FunctionContext.CanAccessBroker(brokerId))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

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
        // Only link to the broker if it actually exists (avoids a FK-constraint 500).
        buyer.BrokerId = brokerId > 0 && await _db.Brokers.AnyAsync(b => b.Id == brokerId) ? brokerId : null;
        _db.Buyers.Add(buyer);
        await _db.SaveChangesAsync();
        await TryPushBuyerToBcAsync(buyer);
        return await CreateJsonResponse(req, buyer, System.Net.HttpStatusCode.Created);
    }

    [Function("GetAllBuyers")]
    public async Task<HttpResponseData> GetAllBuyers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "buyers")] HttpRequestData req)
    {
        var buyers = await _db.Buyers
            .Include(b => b.BrokerBuyers).ThenInclude(bb => bb.Broker)
            .ToListAsync();

        // Same shape for everyone, but bank/tax/payment details are redacted for non-admins
        // (this endpoint feeds the broker "invite customer" list, which only needs name/number).
        var isAdmin = req.FunctionContext.IsAdmin();
        var result = buyers
            .OrderBy(b => int.TryParse(b.BuyerNumber, out var n) ? n : int.MaxValue)
            .Select(b => new
            {
                b.Id, b.BuyerNumber, b.Name, b.Name2, b.SearchName,
                b.ContactName, b.AddressLine1, b.AddressLine2, b.Country, b.PostalCode, b.City,
                b.ContactPhone, b.MobilePhone, b.ContactEmail, b.HomePage,
                VatRegistrationNo = isAdmin ? b.VatRegistrationNo : "",
                RegistrationNo = isAdmin ? b.RegistrationNo : "",
                b.CustomerGroup, b.SalesPerson,
                PaymentTerm = isAdmin ? b.PaymentTerm : "",
                PaymentMethod = isAdmin ? b.PaymentMethod : "",
                b.Currency, b.Language,
                BankName = isAdmin ? b.BankName : "",
                BankAddress = isAdmin ? b.BankAddress : "",
                BankIbanNumber = isAdmin ? b.BankIbanNumber : "",
                SwiftCode = isAdmin ? b.SwiftCode : "",
                BankCountry = isAdmin ? b.BankCountry : "",
                Assignee = isAdmin ? b.Assignee : "",
                AssignmentOfReceivable = isAdmin && b.AssignmentOfReceivable,
                b.IsActive, b.Address,
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
        var result = new
        {
            buyer.Id, buyer.BuyerNumber, buyer.Name, buyer.Name2, buyer.SearchName,
            buyer.ContactName, buyer.AddressLine1, buyer.AddressLine2, buyer.Country, buyer.PostalCode, buyer.City,
            buyer.ContactPhone, buyer.MobilePhone, buyer.ContactEmail, buyer.HomePage, buyer.VatRegistrationNo,
            buyer.RegistrationNo, buyer.CustomerGroup, buyer.SalesPerson, buyer.PaymentTerm, buyer.PaymentMethod,
            buyer.Currency, buyer.Language, buyer.BankName, buyer.BankAddress, buyer.BankIbanNumber, buyer.SwiftCode,
            buyer.BankCountry, buyer.Assignee, buyer.AssignmentOfReceivable, buyer.IsActive,
            buyer.CreditLimit, buyer.Blocked, buyer.GenBusPostingGroup, buyer.VatBusPostingGroup, buyer.CustomerPostingGroup,
            buyer.Address, buyer.BrokerId, buyer.CreatedAt,
            Brokers = buyer.BrokerBuyers.Select(bb => new { bb.Broker.Id, bb.Broker.BrokerNumber, bb.Broker.CompanyName, bb.Broker.ContactEmail, bb.Broker.ContactPhone }).ToList()
        };
        return await CreateJsonResponse(req, result);
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
        buyer.CreditLimit = dto.CreditLimit;
        buyer.Blocked = dto.Blocked;
        buyer.GenBusPostingGroup = dto.GenBusPostingGroup;
        buyer.VatBusPostingGroup = dto.VatBusPostingGroup;
        buyer.CustomerPostingGroup = dto.CustomerPostingGroup;
        // Guard the BrokerId foreign key: a stale link to a deleted broker would fail the FK
        // constraint on save (500). Keep it only if the broker still exists, else unlink (null).
        buyer.BrokerId = dto.BrokerId.HasValue && await _db.Brokers.AnyAsync(b => b.Id == dto.BrokerId.Value)
            ? dto.BrokerId
            : null;
        await _db.SaveChangesAsync();
        await TryPushBuyerToBcAsync(buyer);
        return await CreateJsonResponse(req, buyer);
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
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

    private async Task TryPushBrokerToBcAsync(Broker broker)
    {
        if (_bcSync == null) return;
        try
        {
            await _bcSync.PushSingleBrokerAsync(broker);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push broker {Number} to BC", broker.BrokerNumber);
        }
    }

    private async Task TryPushBuyerToBcAsync(Buyer buyer)
    {
        if (_bcSync == null) return;
        try
        {
            await _bcSync.PushSingleBuyerAsync(buyer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push buyer {Number} to BC", buyer.BuyerNumber);
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
}
