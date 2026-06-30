using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Functions.Auth;
using AuctionSystem.Functions.Services;
using ClosedXML.Excel;
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
    private readonly CatalogFreezeQueue _freezeQueue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CatalogDraftFunctions(CatalogDbContext db, ILogger<CatalogDraftFunctions> logger, CatalogFreezeQueue freezeQueue)
    {
        _db = db;
        _logger = logger;
        _freezeQueue = freezeQueue;
    }

    public record CreateDraftRequest(string? SalesType, string? Gender, string? Group, int? StartRack);
    public record UpdateDraftLotRequest(string? Description, string? Estimate, string? RedLimit, string? Remarks, string? Damages);

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

            // Freeze the matching cataloglots rows into the catalogue's OWN table auction.[Cat_{id}.Lots] —
            // the single source the catalogue reads for its whole life (view / PDF / labels / scanner / import
            // all read it; edits write straight to it). Build the WHERE from only the provided filters so we
            // never bind an untyped DBNull. A blank filter simply omits its clause (= "all").
            var conditions = new List<string>();
            var args = new List<object>();
            int p = 0;
            if (salesType != null) { conditions.Add($"SalesType = {{{p}}}"); args.Add(salesType); p++; }
            if (gender != null) { conditions.Add($"Gender = {{{p}}}"); args.Add(gender); p++; }
            if (group != null) { conditions.Add($"[Group] = {{{p}}}"); args.Add(group); p++; }
            var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

            // Separate batches: a SELECT ... INTO target can't be dropped+recreated, nor a just-added column
            // referenced, within one batch. (The table name is built from the int draft.Id — never user input.)
            var tbl = $"auction.[Cat_{draft.Id}.Lots]";
            await _db.Database.ExecuteSqlRawAsync($"IF OBJECT_ID('{tbl}', 'U') IS NOT NULL DROP TABLE {tbl};");
            await _db.Database.ExecuteSqlRawAsync($"SELECT * INTO {tbl} FROM auction.cataloglots {where};", args.ToArray());
            await _db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE {tbl} ADD Description NVARCHAR(500) NULL, Estimate NVARCHAR(100) NULL, " +
                "RedLimit NVARCHAR(100) NULL, Remarks NVARCHAR(500) NULL, RackPosition NVARCHAR(20) NULL;");

            // Assign rack-position from the Start Rack #: 20 positions per rack, in catalogue order. ONLY
            // showlot lots (IsShow='Yes') get a rack place — they're the ones physically racked. Each
            // (SalesType, Gender) group STARTS A NEW RACK: when the type or gender changes the racking jumps
            // to the next rack at position 1 (it no longer continues the previous group's rack). Within a
            // group it still fills 20 per rack and spills onto the next. A group's starting rack = StartRack
            // + the racks consumed by all earlier groups (in catalogue order).
            var startRack = body?.StartRack is int sr && sr > 0 ? sr : 1;
            await _db.Database.ExecuteSqlRawAsync($@"
                WITH showlots AS (
                    SELECT LotNumber, SalesType, Gender, CatalogSortOrder,
                           (ROW_NUMBER() OVER (PARTITION BY SalesType, Gender ORDER BY CatalogSortOrder, LotNumber) - 1) AS rnInGroup
                    FROM {tbl}
                    WHERE IsShow = 'Yes'
                ),
                grp AS (
                    SELECT SalesType, Gender,
                           ((COUNT(*) - 1) / 20 + 1) AS racksInGroup,
                           MIN(CatalogSortOrder) AS groupOrder
                    FROM showlots
                    GROUP BY SalesType, Gender
                ),
                grpOffset AS (
                    SELECT SalesType, Gender,
                           COALESCE(SUM(racksInGroup) OVER (
                               ORDER BY groupOrder, SalesType, Gender
                               ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS rackOffset
                    FROM grp
                )
                UPDATE d
                SET RackPosition =
                    CAST(({{0}} + go.rackOffset + s.rnInGroup / 20) AS NVARCHAR(10)) + '-' +
                    CAST((s.rnInGroup % 20 + 1) AS NVARCHAR(10))
                FROM {tbl} d
                JOIN showlots s ON s.LotNumber = d.LotNumber
                JOIN grpOffset go
                    ON ISNULL(go.SalesType, '~') = ISNULL(s.SalesType, '~')
                   AND ISNULL(go.Gender, '~') = ISNULL(s.Gender, '~');",
                startRack);

            // Counts frozen on the row — the drafts list reads these (per-catalogue tables can't be GROUP BY'd together).
            var counts = await CatalogCountsAsync(draft.Id);
            draft.LotCount = counts.Lots;
            draft.SkinCount = counts.Skins;
            draft.ShowLotCount = counts.ShowLots;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Created catalogue draft {Id} '{Name}' ({Lots} lots, {Show} show, {Skins} skins)",
                draft.Id, draft.Name, draft.LotCount, draft.ShowLotCount, draft.SkinCount);

            return await Json(req, ToDto(draft, draft.ShowLotCount, await AvailableSkinsAsync(salesType, gender, group)), HttpStatusCode.Created);
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

        // Live "could have been" pool: skins currently eligible (active, Showlot/Storage) for each type
        // slice. One grouped scan, then summed per draft respecting blank (= all) filters. Comparing this to
        // the frozen SkinCount shows when more skins of a catalogue's type have arrived since it was activated.
        List<EligibleGroup> eligible;
        await using (var conn = new SqlConnection(_db.Database.GetConnectionString()))
        {
            eligible = (await conn.QueryAsync<EligibleGroup>(@"
                SELECT SalesType, Gender, [Group] AS GroupName, COUNT(*) AS Cnt
                FROM dbo.SkinTable
                WHERE IsActive = 1 AND BoxStatus IN ('Showlot','Storage')
                GROUP BY SalesType, Gender, [Group]")).ToList();
        }
        int Available(CatalogDraft d) => eligible
            .Where(g => (d.SalesType == null || string.Equals(g.SalesType?.Trim(), d.SalesType, StringComparison.OrdinalIgnoreCase))
                     && (d.Gender    == null || string.Equals(g.Gender?.Trim(),    d.Gender,    StringComparison.OrdinalIgnoreCase))
                     && (d.Group     == null || string.Equals(g.GroupName?.Trim(), d.Group,     StringComparison.OrdinalIgnoreCase)))
            .Sum(g => g.Cnt);

        return await Json(req, drafts.Select(d => ToDto(d, d.ShowLotCount, Available(d))));
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

        // Already Active, or a freeze is already in flight — nothing to do, return current state.
        if (draft.Status != "Active" && draft.Status != "Activating")
        {
            if (_freezeQueue.IsConfigured)
            {
                // Background the freeze: a big catalogue's SELECT ... INTO over a multi-million-row SkinTable
                // overruns the HTTP gateway timeout if done inline. The worker flips it Active when done.
                draft.Status = "Activating";
                await _db.SaveChangesAsync();
                await _freezeQueue.EnqueueAsync(id);
                _logger.LogInformation("Queued freeze for catalogue draft {Id} '{Name}'", draft.Id, draft.Name);
            }
            else
            {
                // No queue configured (local dev) — freeze inline.
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
        }
        return await Json(req, ToDto(draft, draft.ShowLotCount, await AvailableSkinsAsync(draft.SalesType, draft.Gender, draft.Group)));
    }

    // Background freeze worker: runs the heavy SELECT ... INTO off the request thread so a big catalogue's
    // Activate can't overrun the HTTP gateway timeout. Flips the draft Active on success; on failure reverts
    // it to Draft (so the admin can retry) and logs the error. Idempotent — FreezeCatalogAsync drops+rebuilds
    // the Cat_{id}.X tables, so a redelivered message is safe.
    [Function("CatalogFreezeWorker")]
    public async Task CatalogFreezeWorker(
        [QueueTrigger(CatalogFreezeQueue.QueueName, Connection = "AzureWebJobsStorage")] string message)
    {
        if (!int.TryParse(message, out var draftId)) { _logger.LogWarning("Bad catalog-freeze message: {Msg}", message); return; }

        var draft = await _db.CatalogDrafts.FindAsync(draftId);
        if (draft == null) { _logger.LogWarning("Catalog freeze: draft {Id} not found", draftId); return; }
        if (draft.Status != "Activating")
        {
            _logger.LogInformation("Catalog freeze: draft {Id} is {Status}, not Activating; skipping", draftId, draft.Status);
            return;
        }

        try
        {
            await FreezeCatalogAsync(draftId);
            draft.Status = "Active";
            await _db.SaveChangesAsync();
            _logger.LogInformation("Froze catalogue draft {Id} '{Name}' (background)", draft.Id, draft.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background freeze failed for catalogue {Id}; reverting to Draft", draftId);
            draft.Status = "Draft";
            await _db.SaveChangesAsync();
        }
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

        // 1. Lots — for catalogues created the new way, auction.[Cat_{id}.Lots] is built at draft creation and
        //    edited in place, so the freeze must NOT touch it (rebuilding would wipe in-place edits). Only a
        //    legacy draft (created before per-catalogue tables, lots still in CatalogDraftLots) needs it built
        //    here, from cataloglots + the editable fields carried over.
        var lotsExists = await conn.ExecuteScalarAsync<int?>($"SELECT OBJECT_ID('auction.[{lots}]', 'U')") != null;
        if (!lotsExists)
        {
            await ExecSql(conn, $@"
                SELECT * INTO auction.[{lots}] FROM auction.cataloglots
                WHERE LotNumber IN (SELECT LotNumber FROM auction.CatalogDraftLots WHERE DraftId = {draftId});");
            await ExecSql(conn, $@"
                ALTER TABLE auction.[{lots}] ADD Description NVARCHAR(500) NULL, Estimate NVARCHAR(100) NULL,
                                                 RedLimit NVARCHAR(100) NULL, Remarks NVARCHAR(500) NULL,
                                                 RackPosition NVARCHAR(20) NULL;");
            await ExecSql(conn, $@"
                UPDATE t SET t.Description = d.Description, t.Estimate = d.Estimate,
                             t.RedLimit = d.RedLimit, t.Remarks = d.Remarks, t.RackPosition = d.RackPosition
                FROM auction.[{lots}] t
                JOIN auction.CatalogDraftLots d ON d.DraftId = {draftId} AND d.LotNumber = t.LotNumber;");
        }

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
        await using var conn = new SqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync();
        var (table, isCat) = await ResolveLotsTableAsync(conn, id);

        // Keyed on Lot # (the per-catalogue table has no draft row-id); the view edits by Lot #.
        var sql = $@"
            SELECT LotNumber, StringNumber, CatalogSortOrder, SalesType, Gender, [Group], Color, Quality,
                   Clarity, Size, HairLength, Damages, TotalSkins, BoxCount,
                   ISNULL(Description, '') AS Description, ISNULL(Estimate, '') AS Estimate,
                   ISNULL(RedLimit, '') AS RedLimit, ISNULL(Remarks, '') AS Remarks,
                   ISNULL(RackPosition, '') AS RackPosition,
                   CASE WHEN IsShow = 'Yes' THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS IsShow,
                   COUNT(*) OVER (PARTITION BY StringNumber) AS LotsInString,
                   ROW_NUMBER() OVER (PARTITION BY StringNumber ORDER BY CatalogSortOrder) AS LotSequenceInString,
                   SUM(TotalSkins) OVER (PARTITION BY StringNumber) AS StringTotalSkins
            FROM {table}
            {(isCat ? "" : "WHERE DraftId = @id")}
            ORDER BY CatalogSortOrder";

        var lots = (await conn.QueryAsync(sql, new { id })).ToList();
        return await Json(req, lots);
    }

    // Edit a single lot's Description / Estimate / Red Limit / Remarks / Damages, keyed on Lot #. Written
    // straight into the catalogue's own table (auction.[Cat_{id}.Lots]) so view / PDF / import all see it.
    // Allowed while Draft or Active; only In-Auction is locked.
    [RequireRole("Admin")]
    [Function("UpdateCatalogDraftLot")]
    public async Task<HttpResponseData> UpdateLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "catalog/drafts/{id:int}/lots/{lotNumber:int}")] HttpRequestData req, int id, int lotNumber)
    {
        var draft = await _db.CatalogDrafts.FindAsync(id);
        if (draft == null) return req.CreateResponse(HttpStatusCode.NotFound);
        if (draft.Status == "InAuction")
            return await Json(req, new { error = "Catalogue is in an auction and cannot be edited." }, HttpStatusCode.BadRequest);

        var body = await req.ReadFromJsonAsync<UpdateDraftLotRequest>();
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        await using var conn = new SqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync();
        var (table, isCat) = await ResolveLotsTableAsync(conn, id);
        var rows = await conn.ExecuteAsync(
            $@"UPDATE {table}
               SET Description = @Description, Estimate = @Estimate, RedLimit = @RedLimit,
                   Remarks = @Remarks, Damages = @Damages
               WHERE LotNumber = @lotNumber {(isCat ? "" : "AND DraftId = @id")}",
            new
            {
                Description = Clean(body?.Description),
                Estimate = Clean(body?.Estimate),
                RedLimit = Clean(body?.RedLimit),
                Remarks = Clean(body?.Remarks),
                Damages = Clean(body?.Damages),
                lotNumber,
                id
            });
        if (rows == 0) return req.CreateResponse(HttpStatusCode.NotFound);
        return await Json(req, new { ok = true });
    }

    // The catalogue's lots table: the per-catalogue auction.[Cat_{id}.Lots] once built (at creation, going
    // forward), else the legacy auction.CatalogDraftLots for drafts predating the switch. Returns the table
    // name and whether it's the per-catalogue table (which has no DraftId column — callers add that filter
    // only for the legacy table).
    private static async Task<(string Table, bool IsCat)> ResolveLotsTableAsync(SqlConnection conn, int draftId)
    {
        var exists = await conn.ExecuteScalarAsync<int?>($"SELECT OBJECT_ID('auction.[Cat_{draftId}.Lots]', 'U')") != null;
        return exists ? ($"auction.[Cat_{draftId}.Lots]", true) : ("auction.CatalogDraftLots", false);
    }

    // Lots / Skins / ShowLots counts read straight from the catalogue's own table.
    private async Task<(int Lots, int Skins, int ShowLots)> CatalogCountsAsync(int draftId)
    {
        await using var conn = new SqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync();
        var (table, isCat) = await ResolveLotsTableAsync(conn, draftId);
        var row = await conn.QueryFirstAsync($@"
            SELECT COUNT(*) AS Lots, ISNULL(SUM(TotalSkins), 0) AS Skins,
                   ISNULL(SUM(CASE WHEN IsShow = 'Yes' THEN 1 ELSE 0 END), 0) AS ShowLots
            FROM {table} {(isCat ? "" : "WHERE DraftId = @draftId")}", new { draftId });
        return ((int)row.Lots, (int)row.Skins, (int)row.ShowLots);
    }

    // Export a draft's lots to .xlsx for bulk-editing the 4 fields (matched back on Lot # at import).
    [RequireRole("Admin")]
    [Function("ExportCatalogDraftLots")]
    public async Task<HttpResponseData> Export(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog/drafts/{id:int}/export")] HttpRequestData req, int id)
    {
        var draft = await _db.CatalogDrafts.FindAsync(id);
        if (draft == null) return req.CreateResponse(HttpStatusCode.NotFound);

        await using var conn = new SqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync();
        var (table, isCat) = await ResolveLotsTableAsync(conn, id);
        var lots = (await conn.QueryAsync<ExportRow>($@"
            SELECT LotNumber, ISNULL(RackPosition, '') AS RackPosition, SalesType, Gender, [Group] AS GroupName,
                   Description, Estimate, RedLimit, Remarks, HairLength, Size, Quality, Color, Clarity, Damages
            FROM {table} {(isCat ? "" : "WHERE DraftId = @id")}
            ORDER BY CatalogSortOrder, LotNumber", new { id })).ToList();

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Lots");
        var headers = new[] { "Lot #", "Rack", "Type", "Gender", "Group", "Description", "Estimate", "Red Limit", "Remarks" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;

        var r = 2;
        foreach (var l in lots)
        {
            ws.Cell(r, 1).Value = l.LotNumber;
            ws.Cell(r, 2).Value = l.RackPosition ?? "";
            ws.Cell(r, 3).Value = l.SalesType ?? "";
            ws.Cell(r, 4).Value = l.Gender ?? "";
            ws.Cell(r, 5).Value = l.GroupName ?? "";
            // Show the effective description (custom override, else the auto-built catalogue line) so the
            // column isn't blank; whatever's here on import becomes the lot's Description.
            ws.Cell(r, 6).Value = string.IsNullOrWhiteSpace(l.Description) ? AutoDescription(l) : l.Description;
            ws.Cell(r, 7).Value = l.Estimate ?? "";
            ws.Cell(r, 8).Value = l.RedLimit ?? "";
            ws.Cell(r, 9).Value = l.Remarks ?? "";
            r++;
        }
        ws.Columns().AdjustToContents();

        // Read-only Lot..Group + header row: everything is locked by default, so unlock only the 4 editable
        // data cells, then protect the sheet. No password — a user can still unprotect if truly needed.
        if (lots.Count > 0)
            ws.Range(2, 6, r - 1, 9).Style.Protection.Locked = false;
        ws.Protect();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        resp.Headers.Add("Content-Disposition", $"attachment; filename=\"catalog-{id}.xlsx\"");
        await resp.WriteBytesAsync(ms.ToArray());
        return resp;
    }

    // Bulk-update Description / Estimate / Red Limit / Remarks from an uploaded .xlsx, matched by Lot #.
    // Draft-only (activating locks the catalogue). Read-only columns (Rack, Auto Description) are ignored.
    [RequireRole("Admin")]
    [Function("ImportCatalogDraftLots")]
    public async Task<HttpResponseData> Import(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "catalog/drafts/{id:int}/import")] HttpRequestData req, int id)
    {
        var draft = await _db.CatalogDrafts.FindAsync(id);
        if (draft == null) return req.CreateResponse(HttpStatusCode.NotFound);
        if (draft.Status == "InAuction")
            return await Json(req, new { error = "Catalogue is in an auction and cannot be edited." }, HttpStatusCode.BadRequest);

        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms);
        ms.Position = 0;

        XLWorkbook wb;
        try { wb = new XLWorkbook(ms); }
        catch { return await Json(req, new { error = "Not a valid .xlsx file." }, HttpStatusCode.BadRequest); }

        var ws = wb.Worksheets.FirstOrDefault();
        if (ws == null) return await Json(req, new { error = "No sheet found in the file." }, HttpStatusCode.BadRequest);

        var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in ws.Row(1).CellsUsed()) cols[cell.GetString().Trim()] = cell.Address.ColumnNumber;
        int? Col(string name) => cols.TryGetValue(name, out var c) ? c : (int?)null;

        var lotCol = Col("Lot #") ?? Col("Lot") ?? Col("LotNumber");
        if (lotCol == null) return await Json(req, new { error = "Missing a 'Lot #' column." }, HttpStatusCode.BadRequest);
        int? descCol = Col("Description"), estCol = Col("Estimate"), redCol = Col("Red Limit"), remCol = Col("Remarks");

        static string? Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var descEdits = new List<(int Lot, string? Val)>();
        var estEdits = new List<(int Lot, string? Val)>();
        var redEdits = new List<(int Lot, string? Val)>();
        var remEdits = new List<(int Lot, string? Val)>();
        var seen = new HashSet<int>();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            if (!int.TryParse(ws.Cell(row, lotCol.Value).GetString().Trim(), out var lotNum)) continue;
            seen.Add(lotNum);
            if (descCol != null) descEdits.Add((lotNum, Clean(ws.Cell(row, descCol.Value).GetString())));
            if (estCol != null) estEdits.Add((lotNum, Clean(ws.Cell(row, estCol.Value).GetString())));
            if (redCol != null) redEdits.Add((lotNum, Clean(ws.Cell(row, redCol.Value).GetString())));
            if (remCol != null) remEdits.Add((lotNum, Clean(ws.Cell(row, remCol.Value).GetString())));
        }

        // Apply per-column so a column absent from the file leaves its DB value untouched. Chunked to stay
        // under SQL Server's 2100-parameter cap on a big catalogue.
        await using var conn = new SqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync();
        var (table, isCat) = await ResolveLotsTableAsync(conn, id);
        await UpdateColumnAsync(conn, table, isCat, id, "Description", descEdits);
        await UpdateColumnAsync(conn, table, isCat, id, "Estimate", estEdits);
        await UpdateColumnAsync(conn, table, isCat, id, "RedLimit", redEdits);
        await UpdateColumnAsync(conn, table, isCat, id, "Remarks", remEdits);

        _logger.LogInformation("Imported xlsx into catalogue {Id}: {Updated} lots updated", id, seen.Count);
        return await Json(req, new { updated = seen.Count });
    }


    private static object ToDto(CatalogDraft d, int showLotCount, int availableSkins) => new
    {
        d.Id, d.Name, d.SalesType, d.Gender, d.Group, d.LotCount, showLotCount, d.SkinCount, availableSkins, d.Status, d.CreatedAt
    };

    // Live count of skins currently eligible for a catalogue's type filter — the same eligibility the
    // auction.Boxes view uses (active, BoxStatus Showlot/Storage). A blank filter (null) means "all".
    private async Task<int> AvailableSkinsAsync(string? salesType, string? gender, string? group)
    {
        await using var conn = new SqlConnection(_db.Database.GetConnectionString());
        const string sql = @"
            SELECT COUNT(*) FROM dbo.SkinTable
            WHERE IsActive = 1 AND BoxStatus IN ('Showlot','Storage')
              AND (@salesType IS NULL OR SalesType = @salesType)
              AND (@gender    IS NULL OR Gender    = @gender)
              AND (@grp       IS NULL OR [Group]   = @grp)";
        return await conn.ExecuteScalarAsync<int>(sql, new { salesType, gender, grp = group });
    }

    private class EligibleGroup
    {
        public string? SalesType { get; set; }
        public string? Gender { get; set; }
        public string? GroupName { get; set; }
        public int Cnt { get; set; }
    }

    // The auto-built catalogue line — matches the grid's BuildDescription exactly: grading attributes only
    // (Type/Gender/Group are separate columns), joined by " / ".
    private static string AutoDescription(ExportRow l)
    {
        var parts = new[] { l.HairLength, l.Size, l.Quality, l.Color, l.Clarity,
            (l.Damages != null && !l.Damages.Equals("None", StringComparison.OrdinalIgnoreCase)) ? l.Damages : null }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(" / ", parts);
    }

    // Chunked single-column update of the catalogue's lots table, keyed on Lot #. The column name is a fixed
    // internal literal (never user input). Chunked to stay under SQL Server's 2100-parameter cap.
    private static async Task UpdateColumnAsync(SqlConnection conn, string table, bool isCat, int draftId,
        string column, List<(int Lot, string? Val)> edits)
    {
        foreach (var chunk in edits.Chunk(500))
        {
            var values = new List<string>();
            var dp = new DynamicParameters();
            if (!isCat) dp.Add("draftId", draftId);
            int i = 0;
            foreach (var e in chunk)
            {
                values.Add($"(@l{i}, @v{i})");
                dp.Add($"l{i}", e.Lot);
                dp.Add($"v{i}", e.Val, System.Data.DbType.String);
                i++;
            }
            await conn.ExecuteAsync(
                $@"UPDATE t SET t.[{column}] = v.Val
                   FROM {table} t
                   JOIN (VALUES {string.Join(",", values)}) v(LotNumber, Val)
                     ON t.LotNumber = v.LotNumber {(isCat ? "" : "AND t.DraftId = @draftId")}", dp);
        }
    }

    private class ExportRow
    {
        public int LotNumber { get; set; }
        public string RackPosition { get; set; } = "";
        public string? SalesType { get; set; }
        public string? Gender { get; set; }
        public string? GroupName { get; set; }
        public string? Description { get; set; }
        public string? Estimate { get; set; }
        public string? RedLimit { get; set; }
        public string? Remarks { get; set; }
        public string? HairLength { get; set; }
        public string? Size { get; set; }
        public string? Quality { get; set; }
        public string? Color { get; set; }
        public string? Clarity { get; set; }
        public string? Damages { get; set; }
    }

    private static async Task<HttpResponseData> Json(HttpRequestData req, object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }
}
