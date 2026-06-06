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
        return _configuration["TargetCatalogConnectionString"]
            ?? _configuration.GetConnectionString("TargetCatalogConnectionString")
            ?? "";
    }

    [Function("GenerateLots")]
    public async Task<HttpResponseData> GenerateLots(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "api/lots/generate")]
        HttpRequestData req)
    {
        try
        {
            var connectionString = GetCatalogConnectionString();

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogError("TargetCatalogConnectionString missing.");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteStringAsync("Connection string missing.");
                return response;
            }

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

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
                "TRUNCATE TABLE auction.lots;",
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
                    INSERT INTO auction.lots
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
}
