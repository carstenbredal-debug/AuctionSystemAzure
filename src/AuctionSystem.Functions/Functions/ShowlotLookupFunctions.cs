using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dapper;
using AuctionSystem.Functions.Auth;
using System.Net;
using System.Text.Json;

namespace AuctionSystem.Functions.Functions;

// Barcode -> lot lookup for the handheld showlot app. A scanned barcode belongs to a skin in a box;
// if that box is a "Showlot" box, return the lot (from the live catalogue) and its grading attributes.
public class ShowlotLookupFunctions
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ShowlotLookupFunctions> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ShowlotLookupFunctions(IConfiguration configuration, ILogger<ShowlotLookupFunctions> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string GetConnectionString() =>
        _configuration["SqlConnectionString"]
        ?? _configuration["Values:SqlConnectionString"]
        ?? "";

    // Barcode is a query param (not a route segment) so the path stays fixed at /api/showlot —
    // required for the Easy Auth excludedPaths entry on TEST/PROD to match.
    [AllowAnonymous]
    [Function("GetLotByBarcode")]
    public async Task<HttpResponseData> GetByBarcode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "showlot")] HttpRequestData req)
    {
        var barcode = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["barcode"];

        // Missing / non-numeric / unknown barcode behaves the same as "not found".
        if (string.IsNullOrWhiteSpace(barcode) || !long.TryParse(barcode.Trim(), out var code))
            return await Json(req, new { found = false, message = "Do not exist" });

        try
        {
            var connectionString = GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteStringAsync("Connection string missing.");
                return err;
            }

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            // 1. barcode -> the skin's box + box type
            var box = await connection.QueryFirstOrDefaultAsync<BoxRow>(
                "SELECT TOP 1 BoxNumber, BoxType FROM dbo.SkinTable WHERE Barcode = @code AND IsActive = 1",
                new { code });

            if (box is null)
                return await Json(req, new { found = false, message = "Do not exist" });

            // 2. must be a showlot box
            if (!string.Equals(box.BoxType, "Showlot", StringComparison.OrdinalIgnoreCase))
                return await Json(req, new { found = true, isShowlot = false, message = "This is not a showlot" });

            // 3. box -> the lot it belongs to (live catalogue). IncludedBoxNumbers is a CSV; strip spaces
            //    and match the box number as a whole token.
            var lot = await connection.QueryFirstOrDefaultAsync<LotRow>(
                @"SELECT TOP 1 LotNumber, SalesType, Gender, [Group], HairLength, Size, Quality, Color, Clarity, Damages
                  FROM auction.cataloglots
                  WHERE ',' + REPLACE(IncludedBoxNumbers, ' ', '') + ',' LIKE '%,' + @box + ',%'",
                new { box = box.BoxNumber.ToString() });

            if (lot is null)
                return await Json(req, new { found = false, message = "Do not exist" });

            return await Json(req, new
            {
                found = true,
                isShowlot = true,
                lotNumber = lot.LotNumber,
                salesType = lot.SalesType,
                gender = lot.Gender,
                group = lot.Group,
                hairLength = lot.HairLength,
                size = lot.Size,
                quality = lot.Quality,
                color = lot.Color,
                clarity = lot.Clarity,
                damages = lot.Damages
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error looking up lot by barcode {Barcode}", barcode);
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(ex.Message);
            return response;
        }
    }

    private static async Task<HttpResponseData> Json(HttpRequestData req, object body)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }

    private sealed class BoxRow
    {
        public int BoxNumber { get; set; }
        public string? BoxType { get; set; }
    }

    private sealed class LotRow
    {
        public int LotNumber { get; set; }
        public string? SalesType { get; set; }
        public string? Gender { get; set; }
        public string? Group { get; set; }
        public string? HairLength { get; set; }
        public string? Size { get; set; }
        public string? Quality { get; set; }
        public string? Color { get; set; }
        public string? Clarity { get; set; }
        public string? Damages { get; set; }
    }
}
