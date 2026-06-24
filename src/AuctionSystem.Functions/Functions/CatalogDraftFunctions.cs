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
    public record UpdateDraftLotRequest(string? Description, string? Estimate, string? RedLimit, string? Remarks);

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
            var showLotCount = await _db.CatalogDraftLots.CountAsync(l => l.DraftId == draft.Id && l.IsShow == "Yes");
            await _db.SaveChangesAsync();

            _logger.LogInformation("Created catalogue draft {Id} '{Name}' ({Lots} lots, {Show} show, {Skins} skins)",
                draft.Id, draft.Name, draft.LotCount, showLotCount, draft.SkinCount);

            return await Json(req, ToDto(draft, showLotCount), HttpStatusCode.Created);
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

        // Showlot count per draft (IsShow = 'Yes'), computed on the fly so existing drafts are correct too.
        var showCounts = (await _db.CatalogDraftLots
                .Where(l => l.IsShow == "Yes")
                .GroupBy(l => l.DraftId)
                .Select(g => new { DraftId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.DraftId, x => x.Count);

        return await Json(req, drafts.Select(d => ToDto(d, showCounts.TryGetValue(d.Id, out var c) ? c : 0)));
    }

    // Activate a catalogue (Draft -> Active): it becomes usable for an auction. (Freezing the catalogue's
    // skins/boxes/lots happens here in the next step.) Idempotent for an already-Active catalogue; an
    // In-Auction catalogue is locked.
    [RequireRole("Admin")]
    [Function("ActivateCatalogDraft")]
    public async Task<HttpResponseData> Activate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "catalog/drafts/{id:int}/activate")] HttpRequestData req, int id)
    {
        var draft = await _db.CatalogDrafts.FindAsync(id);
        if (draft == null) return req.CreateResponse(HttpStatusCode.NotFound);
        if (draft.Status == "InAuction")
            return await Json(req, new { error = "Catalogue is already in an auction and cannot be changed." }, HttpStatusCode.BadRequest);

        if (draft.Status != "Active")
        {
            try
            {
                await FreezeCatalogAsync(id);   // capture skins/boxes/lots into auction.[Cat_{id}.X]
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Activate freeze failed for catalogue {Id}", id);
                return await Json(req, new { error = "Activate failed: " + ex.Message }, HttpStatusCode.InternalServerError);
            }
            draft.Status = "Active";
            await _db.SaveChangesAsync();
            _logger.LogInformation("Activated + froze catalogue draft {Id} '{Name}'", draft.Id, draft.Name);
        }
        return await Json(req, ToDto(draft, await ShowCount(id)));
    }

    [RequireRole("Admin")]
    [Function("DeleteCatalogDraft")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "catalog/drafts/{id:int}")] HttpRequestData req, int id)
    {
        var draft = await _db.CatalogDrafts.FindAsync(id);
        if (draft == null) return req.CreateResponse(HttpStatusCode.NotFound);
        if (draft.Status == "InAuction")
            return await Json(req, new { error = "Catalogue is in an auction and cannot be deleted." }, HttpStatusCode.BadRequest);
        await DropFrozenCatalogAsync(id);   // drop [Cat_{id}.Lots/.Skins/.Boxes] if it was activated
        _db.CatalogDrafts.Remove(draft);    // cascade removes the frozen draft lots
        await _db.SaveChangesAsync();
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    // ---- Freeze (Activate): capture a catalogue's skins/boxes/lots into auction.[Cat_{id}.X] so it is a
    // stable, reusable unit. Mirrors AuctionFunctions.BuildSnapshotAsync but scoped to this catalogue's
    // lot numbers, so importing the catalogue into an auction is a straight INSERT ... SELECT * (matching
    // shapes) and per-auction reporting keeps reading identically-shaped tables.
    private async Task FreezeCatalogAsync(int draftId)
    {
        await using var conn = new SqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync();

        // Serialize concurrent activations of the same catalogue. A double-click otherwise runs two freezes
        // that race the DROP/SELECT INTO on the same Cat_{id}.X tables -> "Invalid object name". The session
        // lock is released when this connection is disposed/reset.
        await ExecSql(conn, $"EXEC sp_getapplock @Resource = N'freeze_catalog_{draftId}', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 120000;");

        var lots = $"Cat_{draftId}.Lots";
        var skins = $"Cat_{draftId}.Skins";
        var boxes = $"Cat_{draftId}.Boxes";

        // Each statement runs in its OWN batch so every table reference is to an already-committed table
        // (a SELECT ... INTO target referenced later in the SAME batch fails compile-time name resolution).

        // 1. Lots — full cataloglots shape for this catalogue's lot numbers, plus the editable per-lot
        //    fields (Description/Estimate/RedLimit/Remarks) carried over from CatalogDraftLots so they flow
        //    into the auction's [{Num}.Lots] when the catalogue is imported. (ALTER + UPDATE in separate
        //    batches: a just-added column can't be referenced in the same batch.)
        await ExecSql(conn, $"IF OBJECT_ID('auction.[{lots}]', 'U') IS NOT NULL DROP TABLE auction.[{lots}];");
        await ExecSql(conn, $@"
            SELECT * INTO auction.[{lots}] FROM auction.cataloglots
            WHERE LotNumber IN (SELECT LotNumber FROM auction.CatalogDraftLots WHERE DraftId = {draftId});");
        await ExecSql(conn, $@"
            ALTER TABLE auction.[{lots}] ADD Description NVARCHAR(500) NULL, Estimate NVARCHAR(100) NULL,
                                             RedLimit NVARCHAR(100) NULL, Remarks NVARCHAR(500) NULL;");
        await ExecSql(conn, $@"
            UPDATE t SET t.Description = d.Description, t.Estimate = d.Estimate,
                         t.RedLimit = d.RedLimit, t.Remarks = d.Remarks
            FROM auction.[{lots}] t
            JOIN auction.CatalogDraftLots d ON d.DraftId = {draftId} AND d.LotNumber = t.LotNumber;");

        // 2. Skins — live SkinTable for those lots' boxes, frozen now (TRY_CAST: one bad IncludedBoxNumbers
        //    value must not abort the whole statement).
        await ExecSql(conn, $"IF OBJECT_ID('auction.[{skins}]', 'U') IS NOT NULL DROP TABLE auction.[{skins}];");
        await ExecSql(conn, $@"
            SELECT s.* INTO auction.[{skins}]
            FROM dbo.SkinTable s
            WHERE s.IsActive = 1 AND s.BoxNumber IN (
                SELECT TRY_CAST(LTRIM(RTRIM(value)) AS INT)
                FROM auction.[{lots}] CROSS APPLY STRING_SPLIT(IncludedBoxNumbers, ',')
                WHERE TRY_CAST(LTRIM(RTRIM(value)) AS INT) > 0);");
        await ExecSql(conn, $"UPDATE auction.[{skins}] SET Farmer = 'Unknow' WHERE Farmer IS NULL OR LTRIM(RTRIM(Farmer)) = '';");

        // 3. Boxes — aggregated from the frozen skins + location/weight.
        await ExecSql(conn, $"IF OBJECT_ID('auction.[{boxes}]', 'U') IS NOT NULL DROP TABLE auction.[{boxes}];");
        await ExecSql(conn, $@"
            SELECT s.BoxNumber, s.BoxType, s.BoxStatus, s.SalesType, s.[Group], s.Gender, s.Size, s.HairLength,
                   s.Color, s.Quality, s.Clarity, s.Damages, COUNT(*) AS Skins,
                   ISNULL(b.BoxLocation, '') AS BoxLocation, CAST(ISNULL(b.Weight, 0) AS DECIMAL(18,2)) AS BoxWeight
            INTO auction.[{boxes}]
            FROM auction.[{skins}] s
            LEFT JOIN dbo.boxstatingfromkphg b ON b.BoxNumber = s.BoxNumber
            GROUP BY s.BoxNumber, s.BoxType, s.BoxStatus, s.SalesType, s.[Group], s.Gender, s.Size, s.HairLength,
                     s.Color, s.Quality, s.Clarity, s.Damages, b.BoxLocation, b.Weight;");

        _logger.LogInformation("Froze catalogue {Id} (lots/skins/boxes) into per-catalogue snapshot tables", draftId);
    }

    private async Task DropFrozenCatalogAsync(int draftId)
    {
        await using var conn = new SqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync();
        await ExecSql(conn, $@"
            IF OBJECT_ID('auction.[Cat_{draftId}.Boxes]', 'U') IS NOT NULL DROP TABLE auction.[Cat_{draftId}.Boxes];
            IF OBJECT_ID('auction.[Cat_{draftId}.Skins]', 'U') IS NOT NULL DROP TABLE auction.[Cat_{draftId}.Skins];
            IF OBJECT_ID('auction.[Cat_{draftId}.Lots]', 'U')  IS NOT NULL DROP TABLE auction.[Cat_{draftId}.Lots];");
    }

    private static async Task ExecSql(SqlConnection conn, string sql)
    {
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    // The frozen lots for a draft — same shape as catalog/lots (window fields included) so the catalogue
    // view renders identically.
    [RequireRole("Admin")]
    [Function("GetCatalogDraftLots")]
    public async Task<HttpResponseData> GetLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog/drafts/{id:int}/lots")] HttpRequestData req, int id)
    {
        const string sql = @"
            SELECT Id, LotNumber, StringNumber, CatalogSortOrder, SalesType, Gender, [Group], Color, Quality,
                   Clarity, Size, HairLength, Damages, TotalSkins, BoxCount,
                   ISNULL(Description, '') AS Description, ISNULL(Estimate, '') AS Estimate,
                   ISNULL(RedLimit, '') AS RedLimit, ISNULL(Remarks, '') AS Remarks,
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

    // Edit a single lot's Description / Estimate / Red Limit / Remarks. Allowed while Draft or Active;
    // an In-Auction catalogue is locked.
    [RequireRole("Admin")]
    [Function("UpdateCatalogDraftLot")]
    public async Task<HttpResponseData> UpdateLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "catalog/drafts/{id:int}/lots/{lotRowId:int}")] HttpRequestData req, int id, int lotRowId)
    {
        var draft = await _db.CatalogDrafts.FindAsync(id);
        if (draft == null) return req.CreateResponse(HttpStatusCode.NotFound);
        if (draft.Status == "InAuction")
            return await Json(req, new { error = "Catalogue is in an auction and cannot be edited." }, HttpStatusCode.BadRequest);

        var lot = await _db.CatalogDraftLots.FirstOrDefaultAsync(l => l.Id == lotRowId && l.DraftId == id);
        if (lot == null) return req.CreateResponse(HttpStatusCode.NotFound);

        var body = await req.ReadFromJsonAsync<UpdateDraftLotRequest>();
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        lot.Description = Clean(body?.Description);
        lot.Estimate = Clean(body?.Estimate);
        lot.RedLimit = Clean(body?.RedLimit);
        lot.Remarks = Clean(body?.Remarks);
        await _db.SaveChangesAsync();
        return await Json(req, new { ok = true });
    }

    private static object ToDto(CatalogDraft d, int showLotCount) => new
    {
        d.Id, d.Name, d.SalesType, d.Gender, d.Group, d.LotCount, showLotCount, d.SkinCount, d.Status, d.CreatedAt
    };

    private Task<int> ShowCount(int draftId) =>
        _db.CatalogDraftLots.CountAsync(l => l.DraftId == draftId && l.IsShow == "Yes");

    private static async Task<HttpResponseData> Json(HttpRequestData req, object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }
}
