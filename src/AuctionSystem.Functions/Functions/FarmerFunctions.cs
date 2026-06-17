using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Services;
using AuctionSystem.Functions.Auth;
using AuctionSystem.Functions.BusinessCentral.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class FarmerFunctions
{
    private readonly AuctionDbContext _db;
    private readonly AuctionService _auctionService;
    private readonly ILogger<FarmerFunctions> _logger;
    private readonly BusinessCentralSyncService? _bcSync;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FarmerFunctions(AuctionDbContext db, AuctionService auctionService, ILogger<FarmerFunctions> logger, BusinessCentralSyncService? bcSync = null)
    {
        _db = db;
        _auctionService = auctionService;
        _logger = logger;
        _bcSync = bcSync;
    }

    [Function("GetFarmers")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "farmers")] HttpRequestData req)
    {
        var farmers = await _db.Farmers.ToListAsync();
        return await CreateJsonResponse(req, farmers);
    }

    [Function("GetFarmer")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "farmers/{id:int}")] HttpRequestData req, int id)
    {
        var farmer = await _db.Farmers.FirstOrDefaultAsync(s => s.Id == id);
        if (farmer == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, farmer);
    }

    [Function("CreateFarmer")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "farmers")] HttpRequestData req)
    {
        var farmer = await req.ReadFromJsonAsync<Farmer>();
        if (farmer == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        _db.Farmers.Add(farmer);
        await _db.SaveChangesAsync();
        await TryPushFarmerToBcAsync(farmer);
        return await CreateJsonResponse(req, farmer, System.Net.HttpStatusCode.Created);
    }

    [Function("UpdateFarmer")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "farmers/{id:int}")] HttpRequestData req, int id)
    {
        var dto = await req.ReadFromJsonAsync<Farmer>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        var farmer = await _db.Farmers.FindAsync(id);
        if (farmer == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        farmer.FarmerNumber = dto.FarmerNumber;
        farmer.Name = dto.Name;
        farmer.Name2 = dto.Name2;

        farmer.SearchName = dto.SearchName;
        farmer.ContactName = dto.ContactName;
        farmer.AddressLine1 = dto.AddressLine1;
        farmer.AddressLine2 = dto.AddressLine2;
        farmer.Country = dto.Country;
        farmer.PostalCode = dto.PostalCode;
        farmer.City = dto.City;
        farmer.ContactPhone = dto.ContactPhone;
        farmer.MobilePhone = dto.MobilePhone;
        farmer.ContactEmail = dto.ContactEmail;
        farmer.HomePage = dto.HomePage;
        farmer.VatRegistrationNo = dto.VatRegistrationNo;
        farmer.RegistrationNo = dto.RegistrationNo;
        farmer.CustomerGroup = dto.CustomerGroup;
        farmer.SalesPerson = dto.SalesPerson;
        farmer.PaymentTerm = dto.PaymentTerm;
        farmer.PaymentMethod = dto.PaymentMethod;
        farmer.Currency = dto.Currency;
        farmer.Language = dto.Language;
        farmer.BankName = dto.BankName;
        farmer.BankAddress = dto.BankAddress;
        farmer.BankIbanNumber = dto.BankIbanNumber;
        farmer.SwiftCode = dto.SwiftCode;
        farmer.BankCountry = dto.BankCountry;
        farmer.Assignee = dto.Assignee;
        farmer.AssignmentOfReceivable = dto.AssignmentOfReceivable;
        farmer.CreditLimit = dto.CreditLimit;
        farmer.Blocked = dto.Blocked;
        farmer.GenBusPostingGroup = dto.GenBusPostingGroup;
        farmer.VatBusPostingGroup = dto.VatBusPostingGroup;
        farmer.VendorPostingGroup = dto.VendorPostingGroup;
        farmer.IsActive = dto.IsActive;
        await _db.SaveChangesAsync();
        await TryPushFarmerToBcAsync(farmer);
        return await CreateJsonResponse(req, farmer);
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("DeleteFarmer")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "farmers/{id:int}")] HttpRequestData req, int id)
    {
        var farmer = await _db.Farmers.FindAsync(id);
        if (farmer == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        _db.Farmers.Remove(farmer);
        await _db.SaveChangesAsync();
        return req.CreateResponse(System.Net.HttpStatusCode.NoContent);
    }

    [Function("GetLotsByFarmer")]
    public async Task<HttpResponseData> GetLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "farmers/{farmerId:int}/lots")] HttpRequestData req, int farmerId)
    {
        if (!req.FunctionContext.CanAccessFarmer(farmerId))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        var lots = await _auctionService.GetLotsByFarmerAsync(farmerId);
        return await CreateJsonResponse(req, lots);
    }

    private async Task TryPushFarmerToBcAsync(Farmer farmer)
    {
        if (_bcSync == null) return;
        try
        {
            await _bcSync.PushSingleFarmerAsync(farmer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push farmer {Number} to BC", farmer.FarmerNumber);
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
