using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dapper;
using AuctionSystem.Functions.Auth;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Enums;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuctionSystem.Functions.Functions;

// General box/barcode -> lot lookup for the second app. Unlike /api/showlot (which is showlot-only),
// this resolves ANY box to the lot it belongs to across ALL ACTIVE catalogues, by either:
//   GET /api/lot?barcode=12345   (scanned barcode -> the skin's box -> lot)
//   GET /api/lot?box=678         (raw box number -> lot)
// It answers from the Active catalogues (CatalogDraftLots of CatalogDrafts with Status='Active') — the
// pre-auction source of truth for racking. Returns the lot + rackPosition + grading attributes + the
// editable fields, plus boxNumber/boxType/isShowlot.
public class LotLookupFunctions
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LotLookupFunctions> _logger;
    private readonly AuctionDbContext _db;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LotLookupFunctions(IConfiguration configuration, ILogger<LotLookupFunctions> logger, AuctionDbContext db)
    {
        _configuration = configuration;
        _logger = logger;
        _db = db;
    }

    private string GetConnectionString() =>
        _configuration["SqlConnectionString"]
        ?? _configuration["Values:SqlConnectionString"]
        ?? "";

    // Machine auth for the scanner app: x-api-key against SCANNER_API_KEY. Fails closed — 401 if the key
    // isn't configured or the header doesn't match (same pattern as LOT_GEN/EXTERNAL_PRICE). [AllowAnonymous]
    // only opts out of the app-level role enforcement; this is the real gate. The scanner app must send the key.
    private bool CheckScannerApiKey(HttpRequestData req)
    {
        var expected = _configuration["SCANNER_API_KEY"] ?? _configuration["Values:SCANNER_API_KEY"];
        if (string.IsNullOrEmpty(expected)) return false;
        var provided = req.Headers.TryGetValues("x-api-key", out var vals) ? vals.FirstOrDefault() : null;
        return !string.IsNullOrEmpty(provided) && string.Equals(provided, expected, StringComparison.Ordinal);
    }

    // Inputs are query params (not route segments) so the path stays fixed at /api/lot —
    // required for the Easy Auth excludedPaths entry on TEST/PROD to match.
    [AllowAnonymous]
    [Function("GetLotByBoxOrBarcode")]
    public async Task<HttpResponseData> GetLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lot")] HttpRequestData req)
    {
        if (!CheckScannerApiKey(req))
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync("Invalid or missing x-api-key.");
            return unauth;
        }

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

            // box -> lot across ALL ACTIVE catalogues (CatalogDraftLots of Active CatalogDrafts). A box
            // normally belongs to one catalogue; if it overlaps, the lowest lot number wins.
            var lot = await connection.QueryFirstOrDefaultAsync<LotRow>(
                @"SELECT TOP 1 dl.LotNumber, dl.SalesType, dl.Gender, dl.[Group], dl.HairLength, dl.Size,
                         dl.Quality, dl.Color, dl.Clarity, dl.Damages, dl.RackPosition, dl.Description,
                         dl.Estimate, dl.RedLimit, dl.Remarks, d.Name AS CatalogName
                  FROM auction.CatalogDraftLots dl
                  JOIN auction.CatalogDrafts d ON d.Id = dl.DraftId
                  WHERE d.Status = 'Active'
                    AND ',' + REPLACE(dl.IncludedBoxNumbers, ' ', '') + ',' LIKE '%,' + @box + ',%'
                  ORDER BY dl.LotNumber",
                new { box = boxNumber.ToString() });

            if (lot is null)
                return await Json(req, new { found = false, boxNumber, boxType, message = "Do not exist" });

            return await Json(req, new
            {
                found = true,
                catalog = lot.CatalogName,
                boxNumber,
                boxType,
                isShowlot = string.Equals(boxType, "Showlot", StringComparison.OrdinalIgnoreCase),
                lotNumber = lot.LotNumber,
                rackPosition = lot.RackPosition,
                salesType = lot.SalesType,
                gender = lot.Gender,
                group = lot.Group,
                hairLength = lot.HairLength,
                size = lot.Size,
                quality = lot.Quality,
                color = lot.Color,
                clarity = lot.Clarity,
                damages = lot.Damages,
                description = lot.Description,
                estimate = lot.Estimate,
                redLimit = lot.RedLimit,
                remarks = lot.Remarks
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
        public string? RackPosition { get; set; }
        public string? Description { get; set; }
        public string? Estimate { get; set; }
        public string? RedLimit { get; set; }
        public string? Remarks { get; set; }
        public string? CatalogName { get; set; }
    }
}
