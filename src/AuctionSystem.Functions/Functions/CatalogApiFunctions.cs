using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dapper;
using AuctionSystem.Functions.Auth;
using AuctionSystem.Functions.Models;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    // The auctionNumber is interpolated into a table name (it can't be parameterized), so it MUST be
    // validated before use — otherwise it's an injection vector, especially now these endpoints are
    // anonymous. Only a short alphanumeric token is allowed; anything else falls back to the live
    // catalog (auction.cataloglots), which is the active auction's catalog. This is the single guard
    // that makes catalog/* safe to expose publicly.
    private static readonly Regex AuctionNumberPattern = new("^[A-Za-z0-9]{1,20}$", RegexOptions.Compiled);

    private static string GetCatalogTable(System.Collections.Specialized.NameValueCollection query)
    {
        var auctionNumber = query["auctionNumber"];
        if (!string.IsNullOrEmpty(auctionNumber) && AuctionNumberPattern.IsMatch(auctionNumber))
            return $"auction.[{auctionNumber}.Lots]";
        return "auction.cataloglots";
    }

    // When activeAuction=true (and no explicit auctionNumber), resolve the active auction and inject its
    // number so the catalogue reads its snapshot [{Num}.Lots] (the combined imported catalogue, sales order)
    // instead of live cataloglots. Used by the public LotCatalogWeb. Status 1 = AuctionStatus.Active.
    private static async Task ResolveActiveAuctionIntoQueryAsync(System.Collections.Specialized.NameValueCollection query, SqlConnection connection)
    {
        if (!string.IsNullOrEmpty(query["auctionNumber"])) return;
        if (!string.Equals(query["activeAuction"], "true", StringComparison.OrdinalIgnoreCase)) return;
        var num = await connection.ExecuteScalarAsync<string?>(
            "SELECT TOP 1 AuctionNumber FROM auction.Auctions WHERE Status = 1 ORDER BY Id DESC");
        if (!string.IsNullOrEmpty(num)) query["auctionNumber"] = num;
    }

    // Public, read-only catalog data (the active auction). [AllowAnonymous] opts out of AUTH_ENFORCE;
    // safe because the only interpolated value (auctionNumber) is whitelisted in GetCatalogTable and
    // all filters are parameterized.
    [AllowAnonymous]
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

            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            await ResolveActiveAuctionIntoQueryAsync(query, connection);
            var table = GetCatalogTable(query);

            var filters = new Dictionary<string, List<string>>();

            filters["types"] = (await connection.QueryAsync<string>(
                $"SELECT DISTINCT SalesType FROM {table} WHERE SalesType IS NOT NULL ORDER BY SalesType"
            )).ToList();

            filters["genders"] = (await connection.QueryAsync<string>(
                $"SELECT DISTINCT Gender FROM {table} WHERE Gender IS NOT NULL ORDER BY Gender"
            )).ToList();

            filters["groups"] = (await connection.QueryAsync<string>(
                $"SELECT DISTINCT [Group] FROM {table} WHERE [Group] IS NOT NULL ORDER BY [Group]"
            )).ToList();

            filters["colors"] = (await connection.QueryAsync<string>(
                $"SELECT DISTINCT Color FROM {table} WHERE Color IS NOT NULL ORDER BY Color"
            )).ToList();

            filters["qualities"] = (await connection.QueryAsync<string>(
                $"SELECT DISTINCT Quality FROM {table} WHERE Quality IS NOT NULL ORDER BY Quality"
            )).ToList();

            filters["clarities"] = (await connection.QueryAsync<string>(
                $"SELECT DISTINCT Clarity FROM {table} WHERE Clarity IS NOT NULL ORDER BY Clarity"
            )).ToList();

            filters["sizes"] = (await connection.QueryAsync<string>(
                $"SELECT DISTINCT Size FROM {table} WHERE Size IS NOT NULL ORDER BY Size"
            )).ToList();

            filters["hairLengths"] = (await connection.QueryAsync<string>(
                $"SELECT DISTINCT HairLength FROM {table} WHERE HairLength IS NOT NULL ORDER BY HairLength"
            )).ToList();

            filters["damages"] = (await connection.QueryAsync<string>(
                $"SELECT DISTINCT Damages FROM {table} WHERE Damages IS NOT NULL ORDER BY Damages"
            )).ToList();

            _logger.LogInformation("Filters loaded.");

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(filters));
            return response;
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            // Catalog table not created yet (no lot generation has run) -> empty filters, not a 500.
            var empty = req.CreateResponse(HttpStatusCode.OK);
            empty.Headers.Add("Content-Type", "application/json");
            await empty.WriteStringAsync(JsonSerializer.Serialize(new Dictionary<string, List<string>>()));
            return empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading filters");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(ex.Message);
            return response;
        }
    }

    [AllowAnonymous]
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
            await ResolveActiveAuctionIntoQueryAsync(query, connection);

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

                FROM " + GetCatalogTable(query) + @"
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
        catch (SqlException ex) when (ex.Number == 208)
        {
            // Catalog table not created yet -> empty list, not a 500.
            var empty = req.CreateResponse(HttpStatusCode.OK);
            empty.Headers.Add("Content-Type", "application/json");
            await empty.WriteStringAsync("[]");
            return empty;
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

            var sql = $"SELECT COUNT(*) FROM {GetCatalogTable(query)} WHERE 1=1";
            var parameters = new DynamicParameters();

            AddFilterParameters(query, ref sql, parameters);

            var count = await connection.ExecuteScalarAsync<int>(sql, parameters);

            _logger.LogInformation("Generate count: {Count}", count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync($"Found {count} catalog lots matching your filters.");
            return response;
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            // Catalog table not created yet -> 0, not a 500.
            var resp = req.CreateResponse(HttpStatusCode.OK);
            await resp.WriteStringAsync("Found 0 catalog lots matching your filters.");
            return resp;
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
