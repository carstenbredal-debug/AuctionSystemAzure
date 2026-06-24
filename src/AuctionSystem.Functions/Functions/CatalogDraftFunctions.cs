using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Functions.Auth;
using Dapper;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace AuctionSystem.Functions.Functions;

// Catalogue drafts: a frozen copy of auction.cataloglots filtered by SalesType / Gender / Group (any
// blank = "all"). Admin-only. View/PDF (separate endpoints) read the frozen CatalogDraftLots, so a draft
// is immune to catalogue regeneration.
public class CatalogDraftFunctions
{
    private readonly CatalogDbContext _db;
    private readonly ILogger<CatalogDraftFunctions> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CatalogDraftFunctions(CatalogDbContext db, ILogger<CatalogDraftFunctions> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record CreateDraftRequest(string? SalesType, string? Gender, string? Group);

    [RequireRole("Admin")]
    [Function("CreateCatalogDraft")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "catalog/drafts")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<CreateDraftRequest>();
        string? salesType = string.IsNullOrWhiteSpace(body?.SalesType) ? null : body!.SalesType!.Trim();
        string? gender = string.IsNullOrWhiteSpace(body?.Gender) ? null : body!.Gender!.Trim();
        string? group = string.IsNullOrWhiteSpace(body?.Group) ? null : body!.Group!.Trim();

        try
        {
            var draft = new CatalogDraft
            {
                Name = $"{salesType ?? "All"} - {gender ?? "All"} - {group ?? "All"}",
                SalesType = salesType,
                Gender = gender,
                Group = group,
                CreatedAt = DateTime.UtcNow
            };
            _db.CatalogDrafts.Add(draft);
            await _db.SaveChangesAsync();

            // Freeze the matching cataloglots rows. Build the WHERE from only the provided filters so we
            // never bind an untyped DBNull parameter — those are fragile in ExecuteSqlRaw comparisons and
            // were the cause of the 500. A blank filter simply omits its clause (= "all").
            var conditions = new List<string>();
            var args = new List<object> { draft.Id };
            int p = 1;
            if (salesType != null) { conditions.Add($"SalesType = {{{p}}}"); args.Add(salesType); p++; }
            if (gender != null) { conditions.Add($"Gender = {{{p}}}"); args.Add(gender); p++; }
            if (group != null) { conditions.Add($"[Group] = {{{p}}}"); args.Add(group); p++; }
            var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

            var sql = $@"
                INSERT INTO auction.CatalogDraftLots
                    (DraftId, StringNumber, LotNumber, CatalogSortOrder, IsShow, SalesType, Gender, [Group],
                     HairLength, Size, Quality, Color, Clarity, Damages, IncludedBoxNumbers, BoxCount, TotalSkins)
                SELECT {{0}}, StringNumber, LotNumber, CatalogSortOrder, IsShow, SalesType, Gender, [Group],
                       HairLength, Size, Quality, Color, Clarity, Damages, IncludedBoxNumbers, BoxCount, TotalSkins
                FROM auction.cataloglots
                {where}";
            await _db.Database.ExecuteSqlRawAsync(sql, args.ToArray());

            draft.LotCount = await _db.CatalogDraftLots.CountAsync(l => l.DraftId == draft.Id);
            draft.SkinCount = await _db.CatalogDraftLots.Where(l => l.DraftId == draft.Id).SumAsync(l => (int?)l.TotalSkins) ?? 0;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Created catalogue draft {Id} '{Name}' ({Lots} lots, {Skins} skins)",
                draft.Id, draft.Name, draft.LotCount, draft.SkinCount);

            return await Json(req, ToDto(draft), HttpStatusCode.Created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateCatalogDraft failed");
            var resp = req.CreateResponse(HttpStatusCode.InternalServerError);
            await resp.WriteStringAsync("CreateCatalogDraft failed: " + ex.Message);
            return resp;
        }
    }

    [RequireRole("Admin")]
    [Function("ListCatalogDrafts")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog/drafts")] HttpRequestData req)
    {
        var drafts = await _db.CatalogDrafts.AsNoTracking()
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();
        return await Json(req, drafts.Select(ToDto));
    }

    [RequireRole("Admin")]
    [Function("DeleteCatalogDraft")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "catalog/drafts/{id:int}")] HttpRequestData req, int id)
    {
        var draft = await _db.CatalogDrafts.FindAsync(id);
        if (draft == null) return req.CreateResponse(HttpStatusCode.NotFound);
        _db.CatalogDrafts.Remove(draft); // cascade removes the frozen lots
        await _db.SaveChangesAsync();
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    // The frozen lots for a draft — same shape as catalog/lots (window fields included) so the catalogue
    // view renders identically.
    [RequireRole("Admin")]
    [Function("GetCatalogDraftLots")]
    public async Task<HttpResponseData> GetLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog/drafts/{id:int}/lots")] HttpRequestData req, int id)
    {
        const string sql = @"
            SELECT LotNumber, StringNumber, CatalogSortOrder, SalesType, Gender, [Group], Color, Quality,
                   Clarity, Size, HairLength, Damages, TotalSkins, BoxCount,
                   CASE WHEN IsShow = 'Yes' THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS IsShow,
                   COUNT(*) OVER (PARTITION BY StringNumber) AS LotsInString,
                   ROW_NUMBER() OVER (PARTITION BY StringNumber ORDER BY CatalogSortOrder) AS LotSequenceInString,
                   SUM(TotalSkins) OVER (PARTITION BY StringNumber) AS StringTotalSkins
            FROM auction.CatalogDraftLots
            WHERE DraftId = @id
            ORDER BY CatalogSortOrder";

        await using var conn = new SqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync();
        var lots = (await conn.QueryAsync(sql, new { id })).ToList();
        return await Json(req, lots);
    }

    private static object ToDto(CatalogDraft d) => new
    {
        d.Id, d.Name, d.SalesType, d.Gender, d.Group, d.LotCount, d.SkinCount, d.CreatedAt
    };

    private static async Task<HttpResponseData> Json(HttpRequestData req, object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }
}
