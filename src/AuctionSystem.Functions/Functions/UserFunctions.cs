using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Functions.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

public class UserFunctions
{
    private readonly AuctionDbContext _db;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public UserFunctions(AuctionDbContext db)
    {
        _db = db;
    }

    [Function("GetCurrentUser")]
    public async Task<HttpResponseData> GetCurrentUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users/me/{objectId}")] HttpRequestData req, string objectId)
    {
        // Identify the caller from the authenticated principal, NOT the route param (which a
        // caller could set to impersonate anyone). Fall back to the route only pre-enforcement.
        var principal = req.FunctionContext.GetClientPrincipal();
        var oid = principal?.ObjectId ?? objectId;
        var email = principal?.Email ?? objectId;

        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.AzureAdObjectId == oid && u.IsActive);
        // Fallback: try matching by email for External ID users
        if (user == null)
            user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Email == email && u.IsActive);
        if (user == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, user);
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("GetAllUsers")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users")] HttpRequestData req)
    {
        var users = await _db.AppUsers
            .Include(u => u.Broker)
            .Include(u => u.Farmer)
            .Include(u => u.Buyer)
            .OrderBy(u => u.DisplayName)
            .ToListAsync();
        return await CreateJsonResponse(req, users);
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("CreateUser")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "users")] HttpRequestData req)
    {
        var dto = await req.ReadFromJsonAsync<AppUserRequest>();
        if (dto == null || string.IsNullOrEmpty(dto.Email) || string.IsNullOrEmpty(dto.Role))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        if (!AppRole.IsValid(dto.Role))
        {
            var errorResp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await errorResp.WriteStringAsync($"Invalid role. Must be one of: {string.Join(", ", AppRole.All)}");
            return errorResp;
        }

        var existing = await _db.AppUsers.FirstOrDefaultAsync(u => u.AzureAdObjectId == dto.AzureAdObjectId);
        if (existing != null)
        {
            var errorResp = req.CreateResponse(System.Net.HttpStatusCode.Conflict);
            await errorResp.WriteStringAsync("User with this Azure AD Object ID already exists");
            return errorResp;
        }

        var user = new AppUser
        {
            AzureAdObjectId = dto.AzureAdObjectId,
            Email = dto.Email,
            DisplayName = dto.DisplayName,
            Role = dto.Role,
            BrokerId = dto.BrokerId,
            FarmerId = dto.FarmerId,
            BuyerId = dto.BuyerId,
            IsActive = true
        };

        // Auto-create the linked entity if not already specified
        if (dto.Role == AppRole.Broker && dto.BrokerId == null)
        {
            var broker = new Broker
            {
                BrokerNumber = $"B{DateTime.UtcNow:yyyyMMddHHmmss}",
                CompanyName = dto.DisplayName,
                ContactPerson = dto.DisplayName,
                ContactEmail = dto.Email,
            };
            _db.Brokers.Add(broker);
            await _db.SaveChangesAsync();
            user.BrokerId = broker.Id;
        }
        else if (dto.Role == AppRole.Farmer && dto.FarmerId == null)
        {
            var farmer = new Farmer
            {
                FarmerNumber = $"S{DateTime.UtcNow:yyyyMMddHHmmss}",
                Name = dto.DisplayName,
                ContactEmail = dto.Email,
            };
            _db.Farmers.Add(farmer);
            await _db.SaveChangesAsync();
            user.FarmerId = farmer.Id;
        }
        else if (dto.Role == AppRole.Buyer && dto.BuyerId == null)
        {
            var buyer = new Buyer
            {
                BuyerNumber = $"C{DateTime.UtcNow:yyyyMMddHHmmss}",
                Name = dto.DisplayName,
                ContactEmail = dto.Email,
                BrokerId = dto.BrokerId,
            };
            _db.Buyers.Add(buyer);
            await _db.SaveChangesAsync();
            user.BuyerId = buyer.Id;
        }

        _db.AppUsers.Add(user);
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, user, System.Net.HttpStatusCode.Created);
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("UpdateUser")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "users/{id:int}")] HttpRequestData req, int id)
    {
        var dto = await req.ReadFromJsonAsync<AppUserRequest>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var user = await _db.AppUsers.FindAsync(id);
        if (user == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        if (!string.IsNullOrEmpty(dto.Role) && !AppRole.IsValid(dto.Role))
        {
            var errorResp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await errorResp.WriteStringAsync($"Invalid role. Must be one of: {string.Join(", ", AppRole.All)}");
            return errorResp;
        }

        user.AzureAdObjectId = dto.AzureAdObjectId ?? user.AzureAdObjectId;
        user.Email = dto.Email ?? user.Email;
        user.DisplayName = dto.DisplayName ?? user.DisplayName;
        user.Role = dto.Role ?? user.Role;
        user.BrokerId = dto.BrokerId;
        user.FarmerId = dto.FarmerId;
        user.BuyerId = dto.BuyerId;
        user.IsActive = dto.IsActive;

        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, user);
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("DeleteUser")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "users/{id:int}")] HttpRequestData req, int id)
    {
        var user = await _db.AppUsers.FindAsync(id);
        if (user == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // Clean up linked entities
        if (user.BuyerId.HasValue)
        {
            var buyer = await _db.Buyers.FindAsync(user.BuyerId.Value);
            if (buyer != null)
            {
                var requests = await _db.BrokerCustomerRequests.Where(r => r.BuyerId == buyer.Id).ToListAsync();
                _db.BrokerCustomerRequests.RemoveRange(requests);
                _db.Buyers.Remove(buyer);
            }
        }
        if (user.BrokerId.HasValue)
        {
            var broker = await _db.Brokers.FindAsync(user.BrokerId.Value);
            if (broker != null) _db.Brokers.Remove(broker);
        }
        if (user.FarmerId.HasValue)
        {
            var farmer = await _db.Farmers.FindAsync(user.FarmerId.Value);
            if (farmer != null) _db.Farmers.Remove(farmer);
        }

        _db.AppUsers.Remove(user);
        await _db.SaveChangesAsync();
        return req.CreateResponse(System.Net.HttpStatusCode.NoContent);
    }

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("BulkCreateUsersFromEntities")]
    public async Task<HttpResponseData> BulkCreateFromEntities(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "users/bulk-create-from-entities")] HttpRequestData req)
    {
        var linkedBrokerIds = await _db.AppUsers.Where(u => u.BrokerId != null).Select(u => u.BrokerId!.Value).ToListAsync();
        var linkedBuyerIds = await _db.AppUsers.Where(u => u.BuyerId != null).Select(u => u.BuyerId!.Value).ToListAsync();
        var linkedFarmerIds = await _db.AppUsers.Where(u => u.FarmerId != null).Select(u => u.FarmerId!.Value).ToListAsync();

        var unlinkedBrokers = await _db.Brokers.Where(b => !linkedBrokerIds.Contains(b.Id)).ToListAsync();
        var unlinkedBuyers = await _db.Buyers.Where(b => !linkedBuyerIds.Contains(b.Id)).ToListAsync();
        var unlinkedFarmers = await _db.Farmers.Where(f => !linkedFarmerIds.Contains(f.Id)).ToListAsync();

        var created = new List<object>();

        foreach (var broker in unlinkedBrokers)
        {
            var user = new AppUser
            {
                AzureAdObjectId = $"PENDING-broker-{broker.BrokerNumber}",
                Email = broker.ContactEmail ?? $"broker-{broker.BrokerNumber}@pending.local",
                DisplayName = broker.CompanyName ?? broker.BrokerNumber,
                Role = AppRole.Broker,
                BrokerId = broker.Id,
                IsActive = true
            };
            _db.AppUsers.Add(user);
            created.Add(new { type = "Broker", number = broker.BrokerNumber, name = broker.CompanyName });
        }

        foreach (var buyer in unlinkedBuyers)
        {
            var user = new AppUser
            {
                AzureAdObjectId = $"PENDING-buyer-{buyer.BuyerNumber}",
                Email = buyer.ContactEmail ?? $"buyer-{buyer.BuyerNumber}@pending.local",
                DisplayName = buyer.Name ?? buyer.BuyerNumber,
                Role = AppRole.Buyer,
                BuyerId = buyer.Id,
                IsActive = true
            };
            _db.AppUsers.Add(user);
            created.Add(new { type = "Buyer", number = buyer.BuyerNumber, name = buyer.Name });
        }

        foreach (var farmer in unlinkedFarmers)
        {
            var user = new AppUser
            {
                AzureAdObjectId = $"PENDING-farmer-{farmer.FarmerNumber}",
                Email = farmer.ContactEmail ?? $"farmer-{farmer.FarmerNumber}@pending.local",
                DisplayName = farmer.Name ?? farmer.FarmerNumber,
                Role = AppRole.Farmer,
                FarmerId = farmer.Id,
                IsActive = true
            };
            _db.AppUsers.Add(user);
            created.Add(new { type = "Farmer", number = farmer.FarmerNumber, name = farmer.Name });
        }

        await _db.SaveChangesAsync();

        return await CreateJsonResponse(req, new
        {
            createdCount = created.Count,
            brokers = unlinkedBrokers.Count,
            buyers = unlinkedBuyers.Count,
            farmers = unlinkedFarmers.Count,
            created
        }, System.Net.HttpStatusCode.Created);
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

public record AppUserRequest(
    string AzureAdObjectId,
    string Email,
    string DisplayName,
    string Role,
    int? BrokerId,
    int? FarmerId,
    int? BuyerId,
    bool IsActive = true);
