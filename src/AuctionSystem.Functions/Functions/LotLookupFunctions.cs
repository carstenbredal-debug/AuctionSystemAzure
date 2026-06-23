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

// General box/barcode -> lot lookup for the second app. Unlike /api/showlot (which is showlot-only),
// this resolves ANY box to the lot it belongs to in the live catalogue, by either:
//   GET /api/lot?barcode=12345   (scanned barcode -> the skin's box -> lot)
//   GET /api/lot?box=678         (raw box number -> lot)
// Returns the lot + grading attributes, plus boxNumber/boxType and an isShowlot flag so the caller
// can decide. Both inputs are validated as integers, so the cataloglots match stays injection-safe.
public class LotLookupFunctions
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LotLookupFunctions> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LotLookupFunctions(IConfiguration configuration, ILogger<LotLookupFunctions> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string GetConnectionString() =>
        _configuration["SqlConnectionString"]
        ?? _configuration["Values:SqlConnectionString"]
        ?? "";

    // Inputs are query params (not route segments) so the path stays fixed at /api/lot —
    // required for the Easy Auth excludedPaths entry on TEST/PROD to match.
    [AllowAnonymous]
    [Function("GetLotByBoxOrBarcode")]
    public async Task<HttpResponseData> GetLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lot")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var barcodeRaw = query["barcode"];
        var boxRaw = query["box"];

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

            int boxNumber;
            string? boxType;

            if (!string.IsNullOrWhiteSpace(barcodeRaw))
            {
                // Resolve barcode -> the skin's box + box type.
                if (!long.TryParse(barcodeRaw.Trim(), out var code))
                    return await Json(req, new { found = false, message = "Do not exist" });

                var box = await connection.QueryFirstOrDefaultAsync<BoxRow>(
                    "SELECT TOP 1 BoxNumber, BoxType FROM dbo.SkinTable WHERE Barcode = @code AND IsActive = 1",
                    new { code });

                if (box is null)
                    return await Json(req, new { found = false, message = "Do not exist" });

                boxNumber = box.BoxNumber;
                boxType = box.BoxType;
            }
            else if (!string.IsNullOrWhiteSpace(boxRaw))
            {
                // Use the box number directly; look up its type from any active skin in that box.
                if (!int.TryParse(boxRaw.Trim(), out boxNumber))
                    return await Json(req, new { found = false, message = "Do not exist" });

                boxType = await connection.QueryFirstOrDefaultAsync<string>(
                    "SELECT TOP 1 BoxType FROM dbo.SkinTable WHERE BoxNumber = @boxNumber AND IsActive = 1",
                    new { boxNumber });
            }
            else
            {
                return await Json(req, new { found = false, message = "Provide a 'barcode' or 'box' parameter." });
            }

            // box -> the lot it belongs to (live catalogue). IncludedBoxNumbers is a CSV; strip spaces
            // and match the box number as a whole token.
            LotRow? lot;
            try
            {
                lot = await connection.QueryFirstOrDefaultAsync<LotRow>(
                    @"SELECT TOP 1 LotNumber, SalesType, Gender, [Group], HairLength, Size, Quality, Color, Clarity, Damages
                      FROM auction.cataloglots
                      WHERE ',' + REPLACE(IncludedBoxNumbers, ' ', '') + ',' LIKE '%,' + @box + ',%'",
                    new { box = boxNumber.ToString() });
            }
            catch (SqlException ex) when (ex.Number == 208) // catalogue table not built yet
            {
                return await Json(req, new { found = false, boxNumber, boxType, message = "No catalogue available yet." });
            }

            if (lot is null)
                return await Json(req, new { found = false, boxNumber, boxType, message = "Do not exist" });

            return await Json(req, new
            {
                found = true,
                boxNumber,
                boxType,
                isShowlot = string.Equals(boxType, "Showlot", StringComparison.OrdinalIgnoreCase),
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
            _logger.LogError(ex, "Error looking up lot (barcode={Barcode}, box={Box})", barcodeRaw, boxRaw);
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
