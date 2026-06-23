using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Functions.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
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

        // Freeze the matching cataloglots rows (server-side copy — blank filter = no restriction).
        await _db.Database.ExecuteSqlRawAsync(@"
            INSERT INTO auction.CatalogDraftLots
                (DraftId, StringNumber, LotNumber, CatalogSortOrder, IsShow, SalesType, Gender, [Group],
                 HairLength, Size, Quality, Color, Clarity, Damages, IncludedBoxNumbers, BoxCount, TotalSkins)
            SELECT {0}, StringNumber, LotNumber, CatalogSortOrder, IsShow, SalesType, Gender, [Group],
                   HairLength, Size, Quality, Color, Clarity, Damages, IncludedBoxNumbers, BoxCount, TotalSkins
            FROM auction.cataloglots
            WHERE ({1} IS NULL OR SalesType = {1})
              AND ({2} IS NULL OR Gender = {2})
              AND ({3} IS NULL OR [Group] = {3})",
            draft.Id,
            (object?)salesType ?? DBNull.Value,
            (object?)gender ?? DBNull.Value,
            (object?)group ?? DBNull.Value);

        draft.LotCount = await _db.CatalogDraftLots.CountAsync(l => l.DraftId == draft.Id);
        draft.SkinCount = await _db.CatalogDraftLots.Where(l => l.DraftId == draft.Id).SumAsync(l => (int?)l.TotalSkins) ?? 0;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created catalogue draft {Id} '{Name}' ({Lots} lots, {Skins} skins)",
            draft.Id, draft.Name, draft.LotCount, draft.SkinCount);

        return await Json(req, ToDto(draft), HttpStatusCode.Created);
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
