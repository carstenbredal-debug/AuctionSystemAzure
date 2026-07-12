using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Functions.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace AuctionSystem.Functions.Functions;

// Scanner endpoints for LOADING a shipment onto the courier's truck. Flow: the office presses Ship
// (documents emailed, shipment -> "Ready for courier"); the worker loads the boxes from the OUT
// location, scanning each one; when the LAST box/carton is confirmed the shipment flips to Shipped,
// its invoices follow and the OUT location frees.
//
//   GET  /api/courier-load              -> work list: locations ready for courier + all boxes to load
//   GET  /api/courier-load?code=...     -> resolve a scanned code (storage box number, skin barcode,
//                                          or packed carton number)
//   POST /api/courier-load?code=...     -> confirm the box/carton is on the truck (idempotent)
//
// Fixed path (query params only) for Easy Auth excludedPaths on TEST/PROD; auth = x-api-key against
// SCANNER_API_KEY, same as the other scanner endpoints.
public class CourierLoadFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CourierLoadFunctions> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CourierLoadFunctions(AuctionDbContext db, CatalogDbContext catalogDb, IConfiguration configuration, ILogger<CourierLoadFunctions> logger)
    {
        _db = db;
        _catalogDb = catalogDb;
        _configuration = configuration;
        _logger = logger;
    }

    private bool CheckScannerApiKey(HttpRequestData req)
    {
        var expected = _configuration["SCANNER_API_KEY"] ?? _configuration["Values:SCANNER_API_KEY"];
        if (string.IsNullOrEmpty(expected)) return false;
        var provided = req.Headers.TryGetValues("x-api-key", out var vals) ? vals.FirstOrDefault() : null;
        return !string.IsNullOrEmpty(provided) && string.Equals(provided, expected, StringComparison.Ordinal);
    }

    [AllowAnonymous]
    [Function("GetCourierLoadInfo")]
    public async Task<HttpResponseData> GetCourierLoadInfo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "courier-load")] HttpRequestData req)
    {
        if (!CheckScannerApiKey(req))
            return await Unauthorized(req);

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var code = query["code"] ?? query["box"] ?? query["barcode"];
        if (string.IsNullOrWhiteSpace(code))
            return await ListReadyLocationsAsync(req);

        var hit = await ResolveAsync(code);
        if (hit == null)
            return await Json(req, new { found = false, code, message = $"'{code}' is not a box on any shipment that is ready for courier." });

        var (loaded, total) = await LoadProgressAsync(hit.ShipmentId);
        return await Json(req, new
        {
            found = true,
            code,
            boxNumber = hit.DisplayNumber,
            isCarton = hit.PackedBoxId != null,
            shipmentNumber = hit.ShipmentNumber,
            outLocation = hit.OutLocation,
            alreadyLoaded = hit.LoadedAt != null,
            loadedAt = hit.LoadedAt,
            loadedBoxes = loaded,
            totalBoxes = total,
            message = hit.LoadedAt != null
                ? $"{hit.DisplayNumber} is already on the truck ({loaded}/{total})."
                : $"Load {hit.DisplayNumber} from {hit.OutLocation ?? "?"} (shipment {hit.ShipmentNumber}, {loaded}/{total} loaded)."
        });
    }

    [AllowAnonymous]
    [Function("ConfirmCourierLoad")]
    public async Task<HttpResponseData> ConfirmCourierLoad(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "courier-load")] HttpRequestData req)
    {
        if (!CheckScannerApiKey(req))
            return await Unauthorized(req);

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var code = query["code"] ?? query["box"] ?? query["barcode"];
        if (string.IsNullOrWhiteSpace(code))
            return await Json(req, new { success = false, message = "Missing 'code' (box number, barcode or carton number)." });

        var hit = await ResolveAsync(code);
        if (hit == null)
            return await Json(req, new { success = false, code, message = $"'{code}' is not a box on any shipment that is ready for courier." });

        var alreadyLoaded = hit.LoadedAt != null;
        if (!alreadyLoaded)
        {
            if (hit.PackedBoxId != null)
                await _db.Database.ExecuteSqlRawAsync(
                    "UPDATE auction.PackedBoxes SET LoadedAt = SYSUTCDATETIME() WHERE Id = {0} AND LoadedAt IS NULL", hit.PackedBoxId.Value);
            else
                await _db.Database.ExecuteSqlRawAsync(
                    "UPDATE auction.PackingOrderLines SET LoadedAt = SYSUTCDATETIME() WHERE Id = {0} AND LoadedAt IS NULL", hit.LineId!.Value);
        }

        // Last box on the truck -> the shipment SHIPS: invoices follow, the physical state clears
        // and the OUT location frees (occupancy is derived from active statuses).
        var (loaded, total) = await LoadProgressAsync(hit.ShipmentId);
        var shipmentShipped = false;
        if (total > 0 && loaded >= total)
        {
            var shipment = await _db.Shipments.FindAsync(hit.ShipmentId);
            if (shipment != null && shipment.Status == "Ready for courier")
            {
                shipment.Status = "Shipped";
                shipment.ShippedAt = DateTime.UtcNow;

                var shipLines = await _db.ShipmentLines.Where(l => l.ShipmentId == hit.ShipmentId).ToListAsync();
                var invoiceIds = shipLines.Where(l => l.InvoiceId.HasValue).Select(l => l.InvoiceId!.Value).Distinct().ToList();
                var invoices = await _db.Invoices.Where(i => invoiceIds.Contains(i.Id)).ToListAsync();
                foreach (var inv in invoices)
                    inv.ShippingStatus = "Shipped";

                var boxNumbers = await _db.PackingOrders.Where(p => p.ShipmentId == hit.ShipmentId)
                    .SelectMany(p => p.Lines).Select(l => l.BoxNumber).ToListAsync();
                var states = await _db.BoxPhysicalStates.Where(s => boxNumbers.Contains(s.BoxNumber)).ToListAsync();
                _db.BoxPhysicalStates.RemoveRange(states);

                await _db.SaveChangesAsync();
                shipmentShipped = true;
                _logger.LogInformation("Shipment {Number} fully loaded — SHIPPED, location {Loc} freed",
                    shipment.ShipmentNumber, shipment.OutLocation);
            }
        }

        return await Json(req, new
        {
            success = true,
            code,
            boxNumber = hit.DisplayNumber,
            isCarton = hit.PackedBoxId != null,
            shipmentNumber = hit.ShipmentNumber,
            outLocation = hit.OutLocation,
            alreadyLoaded,
            loadedBoxes = loaded,
            totalBoxes = total,
            shipmentShipped,
            message = alreadyLoaded
                ? $"{hit.DisplayNumber} was already on the truck ({loaded}/{total})."
                : shipmentShipped
                    ? $"{hit.DisplayNumber} loaded — ALL {total} boxes on the truck. Shipment {hit.ShipmentNumber} SHIPPED, {hit.OutLocation ?? "the location"} is now free."
                    : $"{hit.DisplayNumber} loaded ({loaded}/{total} for shipment {hit.ShipmentNumber})."
        });
    }

    // Work list: every shipment Ready for courier with its OUT location and all boxes to load.
    private async Task<HttpResponseData> ListReadyLocationsAsync(HttpRequestData req)
    {
        var shipments = await _db.Shipments
            .Include(s => s.Buyer)
            .Where(s => s.Status == "Ready for courier")
            .OrderBy(s => s.OutLocation)
            .ToListAsync();

        var result = new List<object>();
        foreach (var s in shipments)
        {
            var orders = await _db.PackingOrders.Include(p => p.Lines)
                .Where(p => p.ShipmentId == s.Id).ToListAsync();
            var storage = orders.Where(o => o.Type == "Packing").SelectMany(o => o.Lines)
                .Select(l => new { boxNumber = l.BoxNumber.ToString(), lotNumber = l.LotNumber, skins = l.Skins, loaded = l.LoadedAt != null })
                .ToList();
            var orderIds = orders.Where(o => o.Type == "ShowLot").Select(o => o.Id).ToList();
            var cartons = await _db.PackedBoxes.Include(b => b.ShowLots)
                .Where(b => orderIds.Contains(b.PackingOrderId))
                .Select(b => new { cartonNumber = b.BoxNumber, boxType = b.BoxType, grossWeight = b.GrossWeight, skins = b.ShowLots.Sum(l => l.Skins), loaded = b.LoadedAt != null })
                .ToListAsync();

            result.Add(new
            {
                outLocation = s.OutLocation,
                shipmentNumber = s.ShipmentNumber,
                buyerNumber = s.Buyer?.BuyerNumber ?? "",
                buyerName = s.Buyer?.Name ?? "",
                totalBoxes = storage.Count + cartons.Count,
                loadedBoxes = storage.Count(b => b.loaded) + cartons.Count(c => c.loaded),
                storageBoxes = storage,
                cartons
            });
        }

        return await Json(req, new { locations = result });
    }

    // Resolve a scanned code: storage box number, skin barcode (-> its box), or packed carton
    // number — always scoped to shipments in "Ready for courier".
    private async Task<LoadHit?> ResolveAsync(string code)
    {
        code = code.Trim();

        // 1. Storage box number
        if (int.TryParse(code, out var boxNumber) && boxNumber > 0)
        {
            var line = await FindStorageLineAsync(boxNumber);
            if (line != null) return line;
        }

        // 2. Packed carton number (string barcode from the packing step)
        var carton = await _db.PackedBoxes
            .Where(b => b.BoxNumber == code
                        && b.PackingOrder!.Shipment != null
                        && b.PackingOrder.Shipment.Status == "Ready for courier")
            .OrderByDescending(b => b.Id)
            .Select(b => new LoadHit
            {
                PackedBoxId = b.Id,
                DisplayNumber = "Carton " + b.BoxNumber,
                LoadedAt = b.LoadedAt,
                ShipmentId = b.PackingOrder!.Shipment!.Id,
                ShipmentNumber = b.PackingOrder.Shipment.ShipmentNumber,
                OutLocation = b.PackingOrder.Shipment.OutLocation
            })
            .FirstOrDefaultAsync();
        if (carton != null) return carton;

        // 3. Skin barcode -> its box
        if (long.TryParse(code, out var barcode))
        {
            var fromSkin = await _catalogDb.Database
                .SqlQueryRaw<int>("SELECT TOP 1 BoxNumber AS Value FROM dbo.SkinTable WHERE Barcode = {0} AND IsActive = 1", barcode)
                .FirstOrDefaultAsync();
            if (fromSkin > 0 && fromSkin != boxNumber)
                return await FindStorageLineAsync(fromSkin);
        }
        return null;
    }

    private async Task<LoadHit?> FindStorageLineAsync(int boxNumber)
    {
        return await _db.PackingOrderLines
            .Where(l => l.BoxNumber == boxNumber
                        && l.PackingOrder!.Type == "Packing"
                        && l.PackingOrder.Shipment != null
                        && l.PackingOrder.Shipment.Status == "Ready for courier")
            .OrderByDescending(l => l.Id)
            .Select(l => new LoadHit
            {
                LineId = l.Id,
                DisplayNumber = "Box " + l.BoxNumber,
                LoadedAt = l.LoadedAt,
                ShipmentId = l.PackingOrder!.Shipment!.Id,
                ShipmentNumber = l.PackingOrder.Shipment.ShipmentNumber,
                OutLocation = l.PackingOrder.Shipment.OutLocation
            })
            .FirstOrDefaultAsync();
    }

    // Loaded/total physical boxes for the shipment: storage boxes + packed cartons.
    private async Task<(int Loaded, int Total)> LoadProgressAsync(int shipmentId)
    {
        var storage = await _db.PackingOrders
            .Where(p => p.ShipmentId == shipmentId && p.Type == "Packing")
            .SelectMany(p => p.Lines)
            .Select(l => l.LoadedAt)
            .ToListAsync();
        var cartons = await _db.PackedBoxes
            .Where(b => b.PackingOrder!.ShipmentId == shipmentId)
            .Select(b => b.LoadedAt)
            .ToListAsync();
        return (storage.Count(d => d != null) + cartons.Count(d => d != null), storage.Count + cartons.Count);
    }

    private static async Task<HttpResponseData> Unauthorized(HttpRequestData req)
    {
        var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
        await unauth.WriteStringAsync("Invalid or missing x-api-key.");
        return unauth;
    }

    private static async Task<HttpResponseData> Json(HttpRequestData req, object body)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }

    private sealed class LoadHit
    {
        public int? LineId { get; set; }
        public int? PackedBoxId { get; set; }
        public string DisplayNumber { get; set; } = "";
        public DateTime? LoadedAt { get; set; }
        public int ShipmentId { get; set; }
        public string ShipmentNumber { get; set; } = "";
        public string? OutLocation { get; set; }
    }
}
