using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

public class ParameterFunctions
{
    private readonly AuctionDbContext _db;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ParameterFunctions(AuctionDbContext db)
    {
        _db = db;
    }

    [Function("GetParameters")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "parameters")] HttpRequestData req)
    {
        var parameters = await _db.SystemParameters.OrderBy(p => p.Key).ToListAsync();
        return await CreateJsonResponse(req, parameters);
    }

    [Function("UpdateParameter")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "parameters/{id:int}")] HttpRequestData req, int id)
    {
        var dto = await req.ReadFromJsonAsync<SystemParameter>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        var param = await _db.SystemParameters.FindAsync(id);
        if (param == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        param.Value = dto.Value;
        param.Description = dto.Description;
        param.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, param);
    }

    [Function("CreateParameter")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "parameters")] HttpRequestData req)
    {
        var param = await req.ReadFromJsonAsync<SystemParameter>();
        if (param == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        param.UpdatedAt = DateTime.UtcNow;
        _db.SystemParameters.Add(param);
        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, param, System.Net.HttpStatusCode.Created);
    }

    [Function("DeleteParameter")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "parameters/{id:int}")] HttpRequestData req, int id)
    {
        var param = await _db.SystemParameters.FindAsync(id);
        if (param == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        _db.SystemParameters.Remove(param);
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
