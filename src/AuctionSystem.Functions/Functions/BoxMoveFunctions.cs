using AuctionSystem.Domain.Data;
using AuctionSystem.Functions.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace AuctionSystem.Functions.Functions;

// Scanner endpoints for moving boxes from their storage location to the shipment's OUT staging
// location. Fixed paths (/api/box-move) with query params — required for the Easy Auth
// excludedPaths entries on TEST/PROD, same as /api/lot and /api/showlot.
//
//   GET  /api/box-move?box=12345   -> where does this box go? (OUT location, shipment, progress)
//   POST /api/box-move?box=12345   -> confirm the box was moved (sets MovedToOutAt), returns progress
//
// Both take a box number in `box`, or a skin barcode in `barcode` (resolved to its box).
public class BoxMoveFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BoxMoveFunctions> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // A location is only occupied (and its boxes movable) while the shipment is still in-house.
    private static readonly string[] DoneStatuses = { "Shipped", "Delivered", "Cancelled" };

    public BoxMoveFunctions(AuctionDbContext db, CatalogDbContext catalogDb, IConfiguration configuration, ILogger<BoxMoveFunctions> logger)
    {
        _db = db;
        _catalogDb = catalogDb;
        _configuration = configuration;
        _logger = logger;
    }

    // Machine auth for the scanner app: x-api-key against SCANNER_API_KEY. Fails closed.
    private bool CheckScannerApiKey(HttpRequestData req)
    {
        var expected = _configuration["SCANNER_API_KEY"] ?? _configuration["Values:SCANNER_API_KEY"];
        if (string.IsNullOrEmpty(expected)) return false;
        var provided = req.Headers.TryGetValues("x-api-key", out var vals) ? vals.FirstOrDefault() : null;
        return !string.IsNullOrEmpty(provided) && string.Equals(provided, expected, StringComparison.Ordinal);
    }

    [AllowAnonymous]
    [Function("GetBoxMoveInfo")]
    public async Task<HttpResponseData> GetBoxMoveInfo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "box-move")] HttpRequestData req)
    {
        if (!CheckScannerApiKey(req))
            return await Unauthorized(req);

        // No box/barcode at all -> the scanner's WORK LIST: every packing order on an in-house
        // shipment, with per-box move state.
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        if (string.IsNullOrEmpty(query["box"]) && string.IsNullOrEmpty(query["barcode"]))
            return await ListPackingOrdersAsync(req);

        var boxNumber = await ResolveBoxNumberAsync(req);
        if (boxNumber == null)
            return await Json(req, new { found = false, message = "Missing or invalid box / barcode." });

        var line = await FindActiveLineAsync(boxNumber.Value);
        if (line == null)
            return await Json(req, new { found = false, boxNumber, message = $"Box {boxNumber} is not on any active shipment." });
        if (line.OrderType == "ShowLot")
            return await Json(req, new { found = false, boxNumber, isShowlot = true, message = $"Box {boxNumber} is a showlot — it is packed at the show (Showlot flow), not moved from storage." });
        if (line.OrderStatus == "Ready to Pack")
            return await Json(req, new { found = false, boxNumber, notStarted = true, message = $"Packing has not been started for order {line.PackingOrderNumber} — press Start Packing in the system first." });

        var (moved, total) = await ShipmentMoveProgressAsync(line.ShipmentId);

        return await Json(req, new
        {
            found = true,
            boxNumber = line.BoxNumber,
            lotNumber = line.LotNumber,
            skins = line.Skins,
            storageLocation = line.Location,
            outLocation = line.OutLocation,
            shipmentNumber = line.ShipmentNumber,
            packingOrderNumber = line.PackingOrderNumber,
            orderType = line.OrderType,
            alreadyMoved = line.MovedToOutAt != null,
            movedAt = line.MovedToOutAt,
            movedBoxes = moved,
            totalBoxes = total,
            message = line.MovedToOutAt != null
                ? $"Box {line.BoxNumber} was already moved to {line.OutLocation ?? "?"}."
                : line.OutLocation != null
                    ? $"Move box {line.BoxNumber} to {line.OutLocation} (shipment {line.ShipmentNumber})."
                    : $"Shipment {line.ShipmentNumber} has NO outgoing location — ask the office."
        });
    }

    [AllowAnonymous]
    [Function("ConfirmBoxMove")]
    public async Task<HttpResponseData> ConfirmBoxMove(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "box-move")] HttpRequestData req)
    {
        if (!CheckScannerApiKey(req))
            return await Unauthorized(req);

        var boxNumber = await ResolveBoxNumberAsync(req);
        if (boxNumber == null)
            return await Json(req, new { success = false, message = "Missing or invalid box / barcode." });

        var line = await FindActiveLineAsync(boxNumber.Value);
        if (line == null)
            return await Json(req, new { success = false, boxNumber, message = $"Box {boxNumber} is not on any active shipment." });
        if (line.OrderType == "ShowLot")
            return await Json(req, new { success = false, boxNumber, isShowlot = true, message = $"Box {boxNumber} is a showlot — it is packed at the show (Showlot flow), not moved from storage." });
        if (line.OrderStatus == "Ready to Pack")
            return await Json(req, new { success = false, boxNumber, notStarted = true, message = $"Packing has not been started for order {line.PackingOrderNumber} — press Start Packing in the system first." });

        var alreadyMoved = line.MovedToOutAt != null;
        if (!alreadyMoved)
        {
            // Idempotent: only stamp the first confirmation.
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE auction.PackingOrderLines SET MovedToOutAt = SYSUTCDATETIME() WHERE Id = {0} AND MovedToOutAt IS NULL",
                line.LineId);
        }

        // Last box of the ORDER arrived -> the packing order completes automatically (no manual
        // Confirm in the UI).
        var orderCompleted = false;
        var remainingInOrder = await _db.PackingOrderLines
            .CountAsync(l => l.PackingOrderId == line.PackingOrderId && l.MovedToOutAt == null);
        if (remainingInOrder == 0 && line.OrderStatus != "Packed")
        {
            var order = await _db.PackingOrders.FindAsync(line.PackingOrderId);
            if (order != null && order.Status != "Packed")
            {
                order.Status = "Packed";
                await _db.SaveChangesAsync();
                orderCompleted = true;
            }
        }

        var (moved, total) = await ShipmentMoveProgressAsync(line.ShipmentId);
        var allMoved = total > 0 && moved >= total;

        return await Json(req, new
        {
            success = true,
            boxNumber = line.BoxNumber,
            outLocation = line.OutLocation,
            shipmentNumber = line.ShipmentNumber,
            packingOrderNumber = line.PackingOrderNumber,
            alreadyMoved,
            movedBoxes = moved,
            totalBoxes = total,
            allMoved,
            orderCompleted,
            message = alreadyMoved
                ? $"Box {line.BoxNumber} was already at {line.OutLocation ?? "?"} ({moved}/{total} boxes)."
                : orderCompleted
                    ? $"Box {line.BoxNumber} moved to {line.OutLocation ?? "?"} — order {line.PackingOrderNumber} COMPLETE ({moved}/{total} boxes for shipment {line.ShipmentNumber})."
                    : $"Box {line.BoxNumber} moved to {line.OutLocation ?? "?"} ({moved}/{total} boxes for shipment {line.ShipmentNumber})."
        });
    }

    // The scanner's work list: active STORAGE packing orders (shipment not yet shipped) with their
    // boxes and move state, oldest shipment first. ShowLot orders are excluded — those boxes sit on
    // the show racks and go through the Showlot packing flow, not a storage->OUT move.
    private async Task<HttpResponseData> ListPackingOrdersAsync(HttpRequestData req)
    {
        // Only orders where packing has been STARTED (Start Packing pressed -> In Production) are
        // scanner work; Ready to Pack orders stay invisible, Packed ones are done.
        var orders = await _db.PackingOrders
            .Where(p => p.Type == "Packing" && p.Status == "In Production"
                        && p.Shipment != null && !DoneStatuses.Contains(p.Shipment.Status))
            .OrderBy(p => p.Shipment!.CreatedAt).ThenBy(p => p.PackingOrderNumber)
            .Select(p => new
            {
                p.PackingOrderNumber,
                p.Type,
                p.Status,
                ShipmentNumber = p.Shipment!.ShipmentNumber,
                OutLocation = p.Shipment.OutLocation,
                BuyerNumber = p.Shipment.Buyer != null ? p.Shipment.Buyer.BuyerNumber : "",
                BuyerName = p.Shipment.Buyer != null ? p.Shipment.Buyer.Name : "",
                TotalBoxes = p.Lines.Count,
                MovedBoxes = p.Lines.Count(l => l.MovedToOutAt != null),
                Boxes = p.Lines.OrderBy(l => l.BoxNumber).Select(l => new
                {
                    l.BoxNumber,
                    l.LotNumber,
                    l.Skins,
                    StorageLocation = l.Location,
                    Moved = l.MovedToOutAt != null,
                    l.MovedToOutAt
                }).ToList()
            })
            .ToListAsync();

        return await Json(req, new { orders });
    }

    // Box number from ?box=..., or a skin barcode from ?barcode=... resolved via SkinTable.
    private async Task<int?> ResolveBoxNumberAsync(HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        if (int.TryParse(query["box"], out var box) && box > 0)
            return box;

        if (long.TryParse(query["barcode"], out var barcode))
        {
            var fromSkin = await _catalogDb.Database
                .SqlQueryRaw<int>("SELECT TOP 1 BoxNumber AS Value FROM dbo.SkinTable WHERE Barcode = {0} AND IsActive = 1", barcode)
                .FirstOrDefaultAsync();
            if (fromSkin > 0) return fromSkin;
        }
        return null;
    }

    // The box's packing-order line on an in-house shipment, with everything the scanner needs.
    private async Task<ActiveLine?> FindActiveLineAsync(int boxNumber)
    {
        return await _db.Set<Domain.Entities.PackingOrderLine>()
            .Where(l => l.BoxNumber == boxNumber
                        && l.PackingOrder!.Shipment != null
                        && !DoneStatuses.Contains(l.PackingOrder.Shipment.Status))
            .OrderByDescending(l => l.Id)
            .Select(l => new ActiveLine
            {
                LineId = l.Id,
                BoxNumber = l.BoxNumber,
                LotNumber = l.LotNumber,
                Skins = l.Skins,
                Location = l.Location,
                MovedToOutAt = l.MovedToOutAt,
                PackingOrderId = l.PackingOrderId,
                PackingOrderNumber = l.PackingOrder!.PackingOrderNumber,
                OrderType = l.PackingOrder.Type,
                OrderStatus = l.PackingOrder.Status,
                ShipmentId = l.PackingOrder.ShipmentId,
                ShipmentNumber = l.PackingOrder.Shipment!.ShipmentNumber,
                OutLocation = l.PackingOrder.Shipment.OutLocation
            })
            .FirstOrDefaultAsync();
    }

    private async Task<(int Moved, int Total)> ShipmentMoveProgressAsync(int shipmentId)
    {
        // Storage boxes only — showlots are packed at the show, not moved from storage.
        var counts = await _db.PackingOrders
            .Where(p => p.ShipmentId == shipmentId && p.Type == "Packing")
            .SelectMany(p => p.Lines)
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), Moved = g.Count(l => l.MovedToOutAt != null) })
            .FirstOrDefaultAsync();
        return (counts?.Moved ?? 0, counts?.Total ?? 0);
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

    private sealed class ActiveLine
    {
        public int LineId { get; set; }
        public int BoxNumber { get; set; }
        public int LotNumber { get; set; }
        public int Skins { get; set; }
        public string Location { get; set; } = "";
        public DateTime? MovedToOutAt { get; set; }
        public int PackingOrderId { get; set; }
        public string PackingOrderNumber { get; set; } = "";
        public string OrderType { get; set; } = "";
        public string OrderStatus { get; set; } = "";
        public int ShipmentId { get; set; }
        public string ShipmentNumber { get; set; } = "";
        public string? OutLocation { get; set; }
    }
}
