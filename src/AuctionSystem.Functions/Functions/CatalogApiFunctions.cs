using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dapper;
using AuctionSystem.Functions.Models;
using System.Net;
using System.Text.Json;

namespace AuctionSystem.Functions.Functions;

public class CatalogApiFunctions
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CatalogApiFunctions> _logger;

    public CatalogApiFunctions(
        IConfiguration configuration,
        ILogger<CatalogApiFunctions> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string GetCatalogConnectionString()
    {
        return _configuration["SqlConnectionString"]
            ?? _configuration["Values:SqlConnectionString"]
            ?? "";
    }

    [Function("GetCatalogFilters")]
    public async Task<HttpResponseData> GetFilters(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog/filters")]
        HttpRequestData req)
    {
        try
        {
            var connectionString = GetCatalogConnectionString();

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogError("Connection string missing.");
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteStringAsync("Connection string missing.");
                return err;
            }

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var filters = new Dictionary<string, List<string>>();

            filters["types"] = (await connection.QueryAsync<string>(
                "SELECT DISTINCT SalesType FROM auction.cataloglots WHERE SalesType IS NOT NULL ORDER BY SalesType"
            )).ToList();

            filters["genders"] = (await connection.QueryAsync<string>(
                "SELECT DISTINCT Gender FROM auction.cataloglots WHERE Gender IS NOT NULL ORDER BY Gender"
            )).ToList();

            filters["groups"] = (await connection.QueryAsync<string>(
                "SELECT DISTINCT [Group] FROM auction.cataloglots WHERE [Group] IS NOT NULL ORDER BY [Group]"
            )).ToList();

            filters["colors"] = (await connection.QueryAsync<string>(
                "SELECT DISTINCT Color FROM auction.cataloglots WHERE Color IS NOT NULL ORDER BY Color"
            )).ToList();

            filters["qualities"] = (await connection.QueryAsync<string>(
                "SELECT DISTINCT Quality FROM auction.cataloglots WHERE Quality IS NOT NULL ORDER BY Quality"
            )).ToList();

            filters["clarities"] = (await connection.QueryAsync<string>(
                "SELECT DISTINCT Clarity FROM auction.cataloglots WHERE Clarity IS NOT NULL ORDER BY Clarity"
            )).ToList();

            filters["sizes"] = (await connection.QueryAsync<string>(
                "SELECT DISTINCT Size FROM auction.cataloglots WHERE Size IS NOT NULL ORDER BY Size"
            )).ToList();

            filters["hairLengths"] = (await connection.QueryAsync<string>(
                "SELECT DISTINCT HairLength FROM auction.cataloglots WHERE HairLength IS NOT NULL ORDER BY HairLength"
            )).ToList();

            filters["damages"] = (await connection.QueryAsync<string>(
                "SELECT DISTINCT Damages FROM auction.cataloglots WHERE Damages IS NOT NULL ORDER BY Damages"
            )).ToList();

            _logger.LogInformation("Filters loaded.");

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(filters));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading filters");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(ex.Message);
            return response;
        }
    }

    [Function("GetCatalogLotsApi")]
    public async Task<HttpResponseData> GetCatalogLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog/lots")]
        HttpRequestData req)
    {
        try
        {
            var connectionString = GetCatalogConnectionString();

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteStringAsync("Connection string missing.");
                return err;
            }

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

            var sql = @"
                SELECT
                    LotNumber,
                    StringNumber,
                    CatalogSortOrder,
                    SalesType,
                    Gender,
                    [Group],
                    Color,
                    Quality,
                    Clarity,
                    Size,
                    HairLength,
                    Damages,
                    TotalSkins,
                    BoxCount,
                    CASE WHEN IsShow = 'Yes' THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS IsShow,

                    COUNT(*) OVER (
                        PARTITION BY StringNumber
                    ) AS LotsInString,

                    ROW_NUMBER() OVER (
                        PARTITION BY StringNumber
                        ORDER BY CatalogSortOrder
                    ) AS LotSequenceInString,

                    SUM(TotalSkins) OVER (
                        PARTITION BY StringNumber
                    ) AS StringTotalSkins

                FROM auction.cataloglots
                WHERE 1=1";

            var parameters = new DynamicParameters();

            AddFilterParameters(query, ref sql, parameters);

            sql += " ORDER BY CatalogSortOrder";

            var lots = (await connection.QueryAsync(sql, parameters)).ToList();

            _logger.LogInformation("Catalog lots loaded: {Count}", lots.Count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(lots));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading catalog lots");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(ex.Message);
            return response;
        }
    }

    [Function("GetCatalogLotCount")]
    public async Task<HttpResponseData> GenerateCount(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "catalog/count")]
        HttpRequestData req)
    {
        try
        {
            var connectionString = GetCatalogConnectionString();

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteStringAsync("Connection string missing.");
                return err;
            }

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

            var sql = "SELECT COUNT(*) FROM auction.cataloglots WHERE 1=1";
            var parameters = new DynamicParameters();

            AddFilterParameters(query, ref sql, parameters);

            var count = await connection.ExecuteScalarAsync<int>(sql, parameters);

            _logger.LogInformation("Generate count: {Count}", count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync($"Found {count} catalog lots matching your filters.");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GenerateCount");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(ex.Message);
            return response;
        }
    }

    private static void AddFilterParameters(
        System.Collections.Specialized.NameValueCollection query,
        ref string sql,
        DynamicParameters parameters)
    {
        if (!string.IsNullOrEmpty(query["type"]))
        {
            sql += " AND SalesType = @SalesType";
            parameters.Add("SalesType", query["type"]);
        }
        if (!string.IsNullOrEmpty(query["gender"]))
        {
            sql += " AND Gender = @Gender";
            parameters.Add("Gender", query["gender"]);
        }
        if (!string.IsNullOrEmpty(query["group"]))
        {
            sql += " AND [Group] = @Group";
            parameters.Add("Group", query["group"]);
        }
        if (!string.IsNullOrEmpty(query["color"]))
        {
            sql += " AND Color = @Color";
            parameters.Add("Color", query["color"]);
        }
        if (!string.IsNullOrEmpty(query["quality"]))
        {
            sql += " AND Quality = @Quality";
            parameters.Add("Quality", query["quality"]);
        }
        if (!string.IsNullOrEmpty(query["clarity"]))
        {
            sql += " AND Clarity = @Clarity";
            parameters.Add("Clarity", query["clarity"]);
        }
        if (!string.IsNullOrEmpty(query["size"]))
        {
            sql += " AND Size = @Size";
            parameters.Add("Size", query["size"]);
        }
        if (!string.IsNullOrEmpty(query["damage"]))
        {
            sql += " AND Damages = @Damages";
            parameters.Add("Damages", query["damage"]);
        }
        if (!string.IsNullOrEmpty(query["hairLength"]))
        {
            sql += " AND HairLength = @HairLength";
            parameters.Add("HairLength", query["hairLength"]);
        }
    }
}
