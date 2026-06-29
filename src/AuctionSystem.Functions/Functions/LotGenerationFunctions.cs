using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dapper;
using AuctionSystem.Functions.Auth;
using AuctionSystem.Functions.Models;
using AuctionSystem.Functions.Services;
using System.Data;
using System.Net;
using System.Text.Json;

namespace AuctionSystem.Functions.Functions;

public class LotGenerationFunctions
{
    private readonly LotGenerationService _lotGenerationService;
    private readonly CatalogBuildService _catalogBuildService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LotGenerationFunctions> _logger;

    public LotGenerationFunctions(
        LotGenerationService lotGenerationService,
        CatalogBuildService catalogBuildService,
        IConfiguration configuration,
        ILogger<LotGenerationFunctions> logger)
    {
        _lotGenerationService = lotGenerationService;
        _catalogBuildService = catalogBuildService;
        _configuration = configuration;
        _logger = logger;
    }

    private string GetCatalogConnectionString()
    {
        return _configuration["SqlConnectionString"]
            ?? _configuration["Values:SqlConnectionString"]
            ?? "";
    }

    // Machine-to-machine endpoint (an hourly job triggers it). It opts out of the user-principal
    // middleware ([AllowAnonymous]) and authenticates instead with a shared secret in the x-api-key
    // header, matched against the LOT_GEN_API_KEY app setting. Fails closed: if the key isn't configured
    // or doesn't match, it's a 401. Set LOT_GEN_API_KEY on the Function app and send it as x-api-key.
    [AllowAnonymous]
    [Function("GenerateLots")]
    public async Task<HttpResponseData> GenerateLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "lots/generate")]
        HttpRequestData req)
    {
        var expectedKey = _configuration["LOT_GEN_API_KEY"] ?? _configuration["Values:LOT_GEN_API_KEY"];
        var providedKey = req.Headers.TryGetValues("x-api-key", out var keyVals) ? keyVals.FirstOrDefault() : null;
        if (string.IsNullOrEmpty(expectedKey) || !string.Equals(providedKey, expectedKey, StringComparison.Ordinal))
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync("Invalid or missing x-api-key.");
            return unauth;
        }

        try
        {
            var summary = await RunLotGenerationAsync();
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteStringAsync(summary);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GenerateLots");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(ex.ToString());
            return response;
        }
    }

    // Admin-triggered lot generation (the "Generate Lots" button) — same RunLotGenerationAsync as the
    // api-key endpoint, but authenticated via the SWA admin principal so the UI needs no shared key.
    [RequireRole("Admin")]
    [Function("GenerateLotsAdmin")]
    public async Task<HttpResponseData> GenerateLotsAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "lots/generate-admin")] HttpRequestData req)
    {
        try
        {
            var summary = await RunLotGenerationAsync();
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteStringAsync(summary);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GenerateLotsAdmin");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(ex.ToString());
            return response;
        }
    }

    // The 15-minute auto lot-generation timer was removed — catalogue generation is now manual/deliberate
    // (via the GenerateLots / GenerateLotsAdmin endpoints). This prevents an auto-run from TRUNCATE-ing and
    // regenerating a catalogue that's already in flight or snapshotted for an auction.

    // Shared generation worker for both the HTTP endpoint and the 15-minute timer. Serializes via a SQL
    // app-lock (skip if already running) so a scheduled run can't race a manual one on the TRUNCATE +
    // bulk-inserts.
    private async Task<string> RunLotGenerationAsync()
    {
        var connectionString = GetCatalogConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("SqlConnectionString missing.");

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var lockRc = await connection.ExecuteScalarAsync<int>("DECLARE @r int; EXEC @r = sp_getapplock @Resource=N'LotGeneration', @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=5000; SELECT @r;");
        if (lockRc < 0)
        {
            _logger.LogInformation("Lot generation already running; skipping this run.");
            return "Lot generation already running; skipped.";
        }

        _logger.LogInformation("Loading data from database...");

            var boxes = await connection.QueryAsync<BoxRow>(@"
                SELECT
                    BoxNumber,
                    BoxType,
                    SalesType,
                    Gender,
                    [Group],
                    Damages,
                    CAST(Size AS nvarchar(50)) AS Size,
                    HairLength,
                    Color,
                    Quality,
                    Clarity,
                    Skins
                FROM auction.boxes
                WHERE Skins IS NOT NULL;
            ");

            var rules = await connection.QueryAsync<LotSizeRuleDto>(@"
                SELECT
                    RuleID,
                    Gender,
                    Size,
                    MaxBoxes,
                    MaxSkinsPerBox,
                    ShowlotSkins,
                    MaxLotSizeExclShowlot,
                    MaxLotSizeInclShowlot,
                    Priority,
                    IsActive
                FROM auction.lotsizerule
                WHERE IsActive = 1
                ORDER BY Priority;
            ");

            var groupOrders = await connection.QueryAsync<LotGroupOrderDto>(@"
                SELECT
                    ColumnName,
                    GroupOrder
                FROM auction.lotgrouporder
                ORDER BY GroupOrder;
            ");

            var sortOrders = await connection.QueryAsync<LotSortOrderDto>(@"
                SELECT
                    ColumnName,
                    Value,
                    SortOrder
                FROM auction.lotsortorder;
            ");

            var stringDefinitions = await connection.QueryAsync<StringDefinitionDto>(@"
                SELECT
                    StringDefinitionID,
                    ColumnName,
                    IsActive
                FROM auction.stringdefinition
                WHERE IsActive = 1;
            ");

            var catalogNumberRules = await connection.QueryAsync<CatalogNumberRuleDto>(@"
                SELECT
                    CatalogNumberRuleID,
                    SalesType,
                    Gender,
                    [Group],
                    StartNumber,
                    IsActive
                FROM auction.catalognumberrule
                WHERE IsActive = 1;
            ");

            _logger.LogInformation("Generating lots...");

            var generationResult = _lotGenerationService.GenerateLots(
                boxes,
                rules,
                groupOrders,
                sortOrders
            );

            var lots = generationResult.Lots;
            var skippedGroups = generationResult.SkippedGroups;

            _logger.LogInformation("Building catalog...");

            var catalogBuildResult = _catalogBuildService.BuildCatalogLots(
                lots,
                stringDefinitions,
                groupOrders,
                sortOrders,
                catalogNumberRules,
                generationResult.RunId
            );

            var catalogLots = catalogBuildResult.CatalogLots;
            skippedGroups.AddRange(catalogBuildResult.SkippedGroups);

            _logger.LogInformation("Writing results to database...");

            await connection.ExecuteAsync("TRUNCATE TABLE auction.GeneratedLots;");
            await connection.ExecuteAsync("TRUNCATE TABLE auction.lotgenerationskippedgroup;");
            await connection.ExecuteAsync("TRUNCATE TABLE auction.cataloglots;");

            if (lots.Any())
            {
                var table = new DataTable();
                table.Columns.Add("UniqueID", typeof(Guid));
                table.Columns.Add("IsShow", typeof(string));
                table.Columns.Add("ShowlotBoxNumber", typeof(int));
                table.Columns.Add("SalesType", typeof(string));
                table.Columns.Add("Group", typeof(string));
                table.Columns.Add("Gender", typeof(string));
                table.Columns.Add("Size", typeof(string));
                table.Columns.Add("Color", typeof(string));
                table.Columns.Add("Quality", typeof(string));
                table.Columns.Add("Clarity", typeof(string));
                table.Columns.Add("HairLength", typeof(string));
                table.Columns.Add("Damages", typeof(string));
                table.Columns.Add("IncludedBoxNumbers", typeof(string));
                table.Columns.Add("BoxCount", typeof(int));
                table.Columns.Add("TotalSkins", typeof(int));

                foreach (var lot in lots)
                {
                    table.Rows.Add(
                        lot.UniqueID,
                        lot.IsShow,
                        lot.ShowlotBoxNumber.HasValue ? lot.ShowlotBoxNumber.Value : DBNull.Value,
                        lot.SalesType,
                        lot.Group,
                        lot.Gender,
                        lot.Size,
                        lot.Color,
                        lot.Quality,
                        lot.Clarity,
                        lot.HairLength,
                        lot.Damages,
                        lot.IncludedBoxNumbers,
                        lot.BoxCount,
                        lot.TotalSkins
                    );
                }

                using var bulkCopy = new SqlBulkCopy(connection) { DestinationTableName = "auction.GeneratedLots", BatchSize = 1000 };
                bulkCopy.ColumnMappings.Add("UniqueID", "UniqueID");
                bulkCopy.ColumnMappings.Add("IsShow", "IsShow");
                bulkCopy.ColumnMappings.Add("ShowlotBoxNumber", "ShowlotBoxNumber");
                bulkCopy.ColumnMappings.Add("SalesType", "SalesType");
                bulkCopy.ColumnMappings.Add("Group", "Group");
                bulkCopy.ColumnMappings.Add("Gender", "Gender");
                bulkCopy.ColumnMappings.Add("Size", "Size");
                bulkCopy.ColumnMappings.Add("Color", "Color");
                bulkCopy.ColumnMappings.Add("Quality", "Quality");
                bulkCopy.ColumnMappings.Add("Clarity", "Clarity");
                bulkCopy.ColumnMappings.Add("HairLength", "HairLength");
                bulkCopy.ColumnMappings.Add("Damages", "Damages");
                bulkCopy.ColumnMappings.Add("IncludedBoxNumbers", "IncludedBoxNumbers");
                bulkCopy.ColumnMappings.Add("BoxCount", "BoxCount");
                bulkCopy.ColumnMappings.Add("TotalSkins", "TotalSkins");
                await bulkCopy.WriteToServerAsync(table);
            }

            if (catalogLots.Any())
            {
                var table = new DataTable();
                table.Columns.Add("LotUniqueID", typeof(Guid));
                table.Columns.Add("StringNumber", typeof(int));
                table.Columns.Add("LotNumber", typeof(int));
                table.Columns.Add("CatalogSortOrder", typeof(int));
                table.Columns.Add("IsShow", typeof(string));
                table.Columns.Add("SalesType", typeof(string));
                table.Columns.Add("Gender", typeof(string));
                table.Columns.Add("Group", typeof(string));
                table.Columns.Add("HairLength", typeof(string));
                table.Columns.Add("Size", typeof(string));
                table.Columns.Add("Quality", typeof(string));
                table.Columns.Add("Color", typeof(string));
                table.Columns.Add("Clarity", typeof(string));
                table.Columns.Add("Damages", typeof(string));
                table.Columns.Add("IncludedBoxNumbers", typeof(string));
                table.Columns.Add("BoxCount", typeof(int));
                table.Columns.Add("TotalSkins", typeof(int));

                foreach (var lot in catalogLots)
                {
                    table.Rows.Add(
                        lot.LotUniqueID,
                        lot.StringNumber,
                        lot.LotNumber,
                        lot.CatalogSortOrder,
                        lot.IsShow,
                        lot.SalesType,
                        lot.Gender,
                        lot.Group,
                        lot.HairLength,
                        lot.Size,
                        lot.Quality,
                        lot.Color,
                        lot.Clarity,
                        lot.Damages,
                        lot.IncludedBoxNumbers,
                        lot.BoxCount,
                        lot.TotalSkins
                    );
                }

                using var bulkCopy = new SqlBulkCopy(connection) { DestinationTableName = "auction.cataloglots", BatchSize = 1000 };
                bulkCopy.ColumnMappings.Add("LotUniqueID", "LotUniqueID");
                bulkCopy.ColumnMappings.Add("StringNumber", "StringNumber");
                bulkCopy.ColumnMappings.Add("LotNumber", "LotNumber");
                bulkCopy.ColumnMappings.Add("CatalogSortOrder", "CatalogSortOrder");
                bulkCopy.ColumnMappings.Add("IsShow", "IsShow");
                bulkCopy.ColumnMappings.Add("SalesType", "SalesType");
                bulkCopy.ColumnMappings.Add("Gender", "Gender");
                bulkCopy.ColumnMappings.Add("Group", "Group");
                bulkCopy.ColumnMappings.Add("HairLength", "HairLength");
                bulkCopy.ColumnMappings.Add("Size", "Size");
                bulkCopy.ColumnMappings.Add("Quality", "Quality");
                bulkCopy.ColumnMappings.Add("Color", "Color");
                bulkCopy.ColumnMappings.Add("Clarity", "Clarity");
                bulkCopy.ColumnMappings.Add("Damages", "Damages");
                bulkCopy.ColumnMappings.Add("IncludedBoxNumbers", "IncludedBoxNumbers");
                bulkCopy.ColumnMappings.Add("BoxCount", "BoxCount");
                bulkCopy.ColumnMappings.Add("TotalSkins", "TotalSkins");
                await bulkCopy.WriteToServerAsync(table);
            }

            if (skippedGroups.Any())
            {
                var table = new DataTable();
                table.Columns.Add("RunID", typeof(Guid));
                table.Columns.Add("Reason", typeof(string));
                table.Columns.Add("SalesType", typeof(string));
                table.Columns.Add("Group", typeof(string));
                table.Columns.Add("Gender", typeof(string));
                table.Columns.Add("Size", typeof(string));
                table.Columns.Add("Color", typeof(string));
                table.Columns.Add("Quality", typeof(string));
                table.Columns.Add("Clarity", typeof(string));
                table.Columns.Add("HairLength", typeof(string));
                table.Columns.Add("Damages", typeof(string));
                table.Columns.Add("BoxCount", typeof(int));
                table.Columns.Add("ShowlotCount", typeof(int));
                table.Columns.Add("TotalSkins", typeof(int));
                table.Columns.Add("BoxNumbers", typeof(string));

                foreach (var sg in skippedGroups)
                {
                    table.Rows.Add(
                        sg.RunID,
                        sg.Reason,
                        sg.SalesType,
                        sg.Group,
                        sg.Gender,
                        sg.Size,
                        sg.Color,
                        sg.Quality,
                        sg.Clarity,
                        sg.HairLength,
                        sg.Damages,
                        sg.BoxCount,
                        sg.ShowlotCount,
                        sg.TotalSkins,
                        sg.BoxNumbers
                    );
                }

                using var bulkCopy = new SqlBulkCopy(connection) { DestinationTableName = "auction.lotgenerationskippedgroup", BatchSize = 1000 };
                bulkCopy.ColumnMappings.Add("RunID", "RunID");
                bulkCopy.ColumnMappings.Add("Reason", "Reason");
                bulkCopy.ColumnMappings.Add("SalesType", "SalesType");
                bulkCopy.ColumnMappings.Add("Group", "Group");
                bulkCopy.ColumnMappings.Add("Gender", "Gender");
                bulkCopy.ColumnMappings.Add("Size", "Size");
                bulkCopy.ColumnMappings.Add("Color", "Color");
                bulkCopy.ColumnMappings.Add("Quality", "Quality");
                bulkCopy.ColumnMappings.Add("Clarity", "Clarity");
                bulkCopy.ColumnMappings.Add("HairLength", "HairLength");
                bulkCopy.ColumnMappings.Add("Damages", "Damages");
                bulkCopy.ColumnMappings.Add("BoxCount", "BoxCount");
                bulkCopy.ColumnMappings.Add("ShowlotCount", "ShowlotCount");
                bulkCopy.ColumnMappings.Add("TotalSkins", "TotalSkins");
                bulkCopy.ColumnMappings.Add("BoxNumbers", "BoxNumbers");
                await bulkCopy.WriteToServerAsync(table);
            }

            var summary =
                $"Generated lots: {lots.Count}. " +
                $"Catalog lots: {catalogLots.Count}. " +
                $"Skipped groups: {skippedGroups.Count}.";

            if (catalogBuildResult.Warnings.Count > 0)
                summary += " WARNINGS: " + string.Join(" ", catalogBuildResult.Warnings);

            _logger.LogInformation(summary);
            return summary;
    }
}
