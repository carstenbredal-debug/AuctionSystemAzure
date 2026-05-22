using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class UserFunctions
{
    private readonly AuctionDbContext _db;
    private readonly ILogger<UserFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public UserFunctions(AuctionDbContext db, ILogger<UserFunctions> logger)
    {
        _db = db;
        _logger = logger;
    }

    [Function("GetCurrentUser")]
    public async Task<HttpResponseData> GetCurrentUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users/me/{objectId}")] HttpRequestData req, string objectId)
    {
        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.AzureAdObjectId == objectId && u.IsActive);
        if (user == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, user);
    }

    [Function("GetAllUsers")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users")] HttpRequestData req)
    {
        var users = await _db.AppUsers
            .Include(u => u.Broker)
            .Include(u => u.Seller)
            .Include(u => u.Buyer)
            .OrderBy(u => u.DisplayName)
            .ToListAsync();
        return await CreateJsonResponse(req, users);
    }

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
            SellerId = dto.SellerId,
            BuyerId = dto.BuyerId,
            IsActive = true
        };

        _db.AppUsers.Add(user);
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, user, System.Net.HttpStatusCode.Created);
    }

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

        user.Email = dto.Email ?? user.Email;
        user.DisplayName = dto.DisplayName ?? user.DisplayName;
        user.Role = dto.Role ?? user.Role;
        user.BrokerId = dto.BrokerId;
        user.SellerId = dto.SellerId;
        user.BuyerId = dto.BuyerId;
        user.IsActive = dto.IsActive;

        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, user);
    }

    [Function("DeleteUser")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "users/{id:int}")] HttpRequestData req, int id)
    {
        var user = await _db.AppUsers.FindAsync(id);
        if (user == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        _db.AppUsers.Remove(user);
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

public record AppUserRequest(
    string AzureAdObjectId,
    string Email,
    string DisplayName,
    string Role,
    int? BrokerId,
    int? SellerId,
    int? BuyerId,
    bool IsActive = true);
