using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class SellerFunctions
{
    private readonly AuctionDbContext _db;
    private readonly AuctionService _auctionService;
    private readonly ILogger<SellerFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SellerFunctions(AuctionDbContext db, AuctionService auctionService, ILogger<SellerFunctions> logger)
    {
        _db = db;
        _auctionService = auctionService;
        _logger = logger;
    }

    [Function("GetSellers")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sellers")] HttpRequestData req)
    {
        var sellers = await _db.Sellers.ToListAsync();
        return await CreateJsonResponse(req, sellers);
    }

    [Function("GetSeller")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sellers/{id:int}")] HttpRequestData req, int id)
    {
        var seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Id == id);
        if (seller == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, seller);
    }

    [Function("CreateSeller")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sellers")] HttpRequestData req)
    {
        var seller = await req.ReadFromJsonAsync<Seller>();
        if (seller == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, seller, System.Net.HttpStatusCode.Created);
    }

    [Function("UpdateSeller")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sellers/{id:int}")] HttpRequestData req, int id)
    {
        var dto = await req.ReadFromJsonAsync<Seller>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        var seller = await _db.Sellers.FindAsync(id);
        if (seller == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        seller.SellerNumber = dto.SellerNumber;
        seller.Name = dto.Name;
        seller.Name2 = dto.Name2;

        seller.SearchName = dto.SearchName;
        seller.ContactName = dto.ContactName;
        seller.AddressLine1 = dto.AddressLine1;
        seller.AddressLine2 = dto.AddressLine2;
        seller.Country = dto.Country;
        seller.PostalCode = dto.PostalCode;
        seller.City = dto.City;
        seller.ContactPhone = dto.ContactPhone;
        seller.MobilePhone = dto.MobilePhone;
        seller.ContactEmail = dto.ContactEmail;
        seller.HomePage = dto.HomePage;
        seller.VatRegistrationNo = dto.VatRegistrationNo;
        seller.RegistrationNo = dto.RegistrationNo;
        seller.CustomerGroup = dto.CustomerGroup;
        seller.SalesPerson = dto.SalesPerson;
        seller.PaymentTerm = dto.PaymentTerm;
        seller.PaymentMethod = dto.PaymentMethod;
        seller.Currency = dto.Currency;
        seller.Language = dto.Language;
        seller.BankName = dto.BankName;
        seller.BankAddress = dto.BankAddress;
        seller.BankIbanNumber = dto.BankIbanNumber;
        seller.SwiftCode = dto.SwiftCode;
        seller.BankCountry = dto.BankCountry;
        seller.Assignee = dto.Assignee;
        seller.AssignmentOfReceivable = dto.AssignmentOfReceivable;
        seller.IsActive = dto.IsActive;
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, seller);
    }

    [Function("DeleteSeller")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sellers/{id:int}")] HttpRequestData req, int id)
    {
        var seller = await _db.Sellers.FindAsync(id);
        if (seller == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        _db.Sellers.Remove(seller);
        await _db.SaveChangesAsync();
        return req.CreateResponse(System.Net.HttpStatusCode.NoContent);
    }

    [Function("GetLotsBySeller")]
    public async Task<HttpResponseData> GetLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sellers/{sellerId:int}/lots")] HttpRequestData req, int sellerId)
    {
        var lots = await _auctionService.GetLotsBySellerAsync(sellerId);
        return await CreateJsonResponse(req, lots);
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
