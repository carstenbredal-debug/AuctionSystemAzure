using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dapper;
using AuctionSystem.Functions.Models;
using AuctionSystem.Functions.Services;
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

    [Function("GenerateLots")]
    public async Task<HttpResponseData> GenerateLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "lots/generate")]
        HttpRequestData req)
    {
        try
        {
            var connectionString = GetCatalogConnectionString();

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogError("SqlConnectionString missing.");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteStringAsync("Connection string missing.");
                return response;
            }

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            // Refresh auction.boxes from dbo.SkinTable before generating lots
            _logger.LogInformation("Refreshing auction.boxes from dbo.SkinTable...");
            await RefreshBoxTableAsync(connection);

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

            using var transaction = connection.BeginTransaction();

            await connection.ExecuteAsync(
                "TRUNCATE TABLE auction.GeneratedLots;",
                transaction: transaction
            );

            await connection.ExecuteAsync(
                "TRUNCATE TABLE auction.lotgenerationskippedgroup;",
                transaction: transaction
            );

            await connection.ExecuteAsync(
                "TRUNCATE TABLE auction.cataloglots;",
                transaction: transaction
            );

            if (lots.Any())
            {
                await connection.ExecuteAsync(@"
                    INSERT INTO auction.GeneratedLots
                    (
                        UniqueID,
                        IsShow,
                        ShowlotBoxNumber,
                        SalesType,
                        [Group],
                        Gender,
                        Size,
                        Color,
                        Quality,
                        Clarity,
                        HairLength,
                        Damages,
                        IncludedBoxNumbers,
                        BoxCount,
                        TotalSkins
                    )
                    VALUES
                    (
                        @UniqueID,
                        @IsShow,
                        @ShowlotBoxNumber,
                        @SalesType,
                        @Group,
                        @Gender,
                        @Size,
                        @Color,
                        @Quality,
                        @Clarity,
                        @HairLength,
                        @Damages,
                        @IncludedBoxNumbers,
                        @BoxCount,
                        @TotalSkins
                    );
                ", lots, transaction: transaction);
            }

            if (catalogLots.Any())
            {
                await connection.ExecuteAsync(@"
                    INSERT INTO auction.cataloglots
                    (
                        LotUniqueID,
                        StringNumber,
                        LotNumber,
                        CatalogSortOrder,
                        IsShow,
                        SalesType,
                        Gender,
                        [Group],
                        HairLength,
                        Size,
                        Quality,
                        Color,
                        Clarity,
                        Damages,
                        IncludedBoxNumbers,
                        BoxCount,
                        TotalSkins
                    )
                    VALUES
                    (
                        @LotUniqueID,
                        @StringNumber,
                        @LotNumber,
                        @CatalogSortOrder,
                        @IsShow,
                        @SalesType,
                        @Gender,
                        @Group,
                        @HairLength,
                        @Size,
                        @Quality,
                        @Color,
                        @Clarity,
                        @Damages,
                        @IncludedBoxNumbers,
                        @BoxCount,
                        @TotalSkins
                    );
                ", catalogLots, transaction: transaction);
            }

            if (skippedGroups.Any())
            {
                await connection.ExecuteAsync(@"
                    INSERT INTO auction.lotgenerationskippedgroup
                    (
                        RunID,
                        Reason,
                        SalesType,
                        [Group],
                        Gender,
                        Size,
                        Color,
                        Quality,
                        Clarity,
                        HairLength,
                        Damages,
                        BoxCount,
                        ShowlotCount,
                        TotalSkins,
                        BoxNumbers
                    )
                    VALUES
                    (
                        @RunID,
                        @Reason,
                        @SalesType,
                        @Group,
                        @Gender,
                        @Size,
                        @Color,
                        @Quality,
                        @Clarity,
                        @HairLength,
                        @Damages,
                        @BoxCount,
                        @ShowlotCount,
                        @TotalSkins,
                        @BoxNumbers
                    );
                ", skippedGroups, transaction: transaction);
            }

            transaction.Commit();

            var summary =
                $"Generated lots: {lots.Count}. " +
                $"Catalog lots: {catalogLots.Count}. " +
                $"Skipped groups: {skippedGroups.Count}.";

            _logger.LogInformation(summary);

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

    private async Task RefreshBoxTableAsync(SqlConnection connection)
    {
        await connection.ExecuteAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'auction')
                EXEC('CREATE SCHEMA auction');");

        await connection.ExecuteAsync(@"
            IF OBJECT_ID('auction.Boxes', 'U') IS NULL
            BEGIN
                CREATE TABLE auction.Boxes (
                    BoxNumber INT NOT NULL PRIMARY KEY,
                    BoxType NVARCHAR(100) NULL,
                    SalesType NVARCHAR(100) NULL,
                    [Group] NVARCHAR(100) NULL,
                    Gender NVARCHAR(100) NULL,
                    Size NVARCHAR(100) NULL,
                    HairLength NVARCHAR(100) NULL,
                    Color NVARCHAR(100) NULL,
                    Quality NVARCHAR(100) NULL,
                    Clarity NVARCHAR(100) NULL,
                    Damages NVARCHAR(100) NULL,
                    Skins INT NOT NULL DEFAULT 0,
                    LastRefreshedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
                );
                CREATE INDEX IX_Boxes_BoxType ON auction.Boxes(BoxType);
                CREATE INDEX IX_Boxes_SalesType_Gender_Group ON auction.Boxes(SalesType, Gender, [Group]);
            END");

        await connection.ExecuteAsync(@"
            TRUNCATE TABLE auction.Boxes;

            INSERT INTO auction.Boxes (BoxNumber, BoxType, SalesType, [Group], Gender, Size, HairLength, Color, Quality, Clarity, Damages, Skins, LastRefreshedAt)
            SELECT
                s.BoxNumber, s.BoxType, s.SalesType, s.[Group], s.Gender,
                CAST(s.Size AS NVARCHAR(100)), s.HairLength, s.Color, s.Quality, s.Clarity, s.Damages,
                COUNT(*), GETUTCDATE()
            FROM dbo.SkinTable s
            WHERE s.BoxStatus IN ('Showlot', 'Storage') AND s.IsActive = 1
            GROUP BY s.BoxNumber, s.BoxType, s.SalesType, s.[Group], s.Gender,
                s.Size, s.HairLength, s.Color, s.Quality, s.Clarity, s.Damages;",
            commandTimeout: 300);

        _logger.LogInformation("Refreshed auction.Boxes");
    }
}
