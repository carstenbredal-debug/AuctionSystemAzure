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

// Scanner endpoints for SHOWLOT packing at the show. Physical flow: the operator picks an empty
// box (Small/Large), scans its barcode (= the new box number), adds one or more showlots, closes
// and weighs it, and puts it at the shipment's OUT location — repeating until the order is empty.
//
//   GET  /api/showlot-pack                    -> work list: In Production ShowLot orders + box types
//   GET  /api/showlot-pack?box=|barcode=      -> resolve a scanned showlot (which order, packed?)
//   POST /api/showlot-pack                    -> close a packed box (order, type, box number, weight,
//                                                showlot box numbers); auto-completes the order
//
// Fixed path (query params only) for the Easy Auth excludedPaths on TEST/PROD; auth = x-api-key
// against SCANNER_API_KEY, same as /api/lot, /api/showlot and /api/box-move.
public class ShowlotPackFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ShowlotPackFunctions> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string[] DoneStatuses = { "Shipped", "Delivered", "Cancelled" };

    private readonly Services.ShipmentReadyService _readyService;

    public ShowlotPackFunctions(AuctionDbContext db, CatalogDbContext catalogDb, IConfiguration configuration, ILogger<ShowlotPackFunctions> logger, Services.ShipmentReadyService readyService)
    {
        _db = db;
        _catalogDb = catalogDb;
        _configuration = configuration;
        _logger = logger;
        _readyService = readyService;
    }

    private bool CheckScannerApiKey(HttpRequestData req)
    {
        var expected = _configuration["SCANNER_API_KEY"] ?? _configuration["Values:SCANNER_API_KEY"];
        if (string.IsNullOrEmpty(expected)) return false;
        var provided = req.Headers.TryGetValues("x-api-key", out var vals) ? vals.FirstOrDefault() : null;
        return !string.IsNullOrEmpty(provided) && string.Equals(provided, expected, StringComparison.Ordinal);
    }

    [AllowAnonymous]
    [Function("GetShowlotPackInfo")]
    public async Task<HttpResponseData> GetShowlotPackInfo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "showlot-pack")] HttpRequestData req)
    {
        if (!CheckScannerApiKey(req))
            return await Unauthorized(req);

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        if (string.IsNullOrEmpty(query["box"]) && string.IsNullOrEmpty(query["barcode"]))
            return await ListOrdersAsync(req);

        var boxNumber = await ResolveBoxNumberAsync(query);
        if (boxNumber == null)
            return await Json(req, new { found = false, message = "Missing or invalid box / barcode." });

        var line = await FindShowlotLineAsync(boxNumber.Value);
        if (line == null)
            return await Json(req, new { found = false, boxNumber, message = $"Box {boxNumber} is not a showlot on any active shipment." });
        if (line.OrderStatus == "Ready to Pack")
            return await Json(req, new { found = false, boxNumber, notStarted = true, message = $"Packing has not been started for order {line.PackingOrderNumber} — press Start Packing in the system first." });

        return await Json(req, new
        {
            found = true,
            boxNumber = line.BoxNumber,
            lotNumber = line.LotNumber,
            skins = line.Skins,
            packingOrderNumber = line.PackingOrderNumber,
            shipmentNumber = line.ShipmentNumber,
            outLocation = line.OutLocation,
            alreadyPacked = line.PackedBoxId != null,
            packedBoxNumber = line.PackedBoxNumber,
            message = line.PackedBoxId != null
                ? $"Showlot {line.BoxNumber} (lot {line.LotNumber}) is already packed in box {line.PackedBoxNumber ?? "?"}."
                : $"Showlot {line.BoxNumber} (lot {line.LotNumber}, {line.Skins} skins) — pack for order {line.PackingOrderNumber}, goes to {line.OutLocation ?? "?"}."
        });
    }

    // Close a packed box: creates the PackedBox, assigns the scanned showlots to it, and completes
    // the order automatically when its last showlot is packed (shipment leaves ShowLot Packing).
    [AllowAnonymous]
    [Function("CloseShowlotPackBox")]
    public async Task<HttpResponseData> CloseShowlotPackBox(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "showlot-pack")] HttpRequestData req)
    {
        if (!CheckScannerApiKey(req))
            return await Unauthorized(req);

        var body = await req.ReadFromJsonAsync<ClosePackBoxDto>();
        if (body == null || string.IsNullOrWhiteSpace(body.PackingOrderNumber) || string.IsNullOrWhiteSpace(body.BoxType)
            || string.IsNullOrWhiteSpace(body.BoxNumber) || body.ShowLotBoxNumbers == null || body.ShowLotBoxNumbers.Count == 0)
            return await Json(req, new { success = false, message = "packingOrderNumber, boxType, boxNumber and showLotBoxNumbers are required." });

        var order = await _db.PackingOrders
            .Include(o => o.Lines)
            .Include(o => o.Shipment)
            .FirstOrDefaultAsync(o => o.PackingOrderNumber == body.PackingOrderNumber && o.Type == "ShowLot");
        if (order == null)
            return await Json(req, new { success = false, message = $"ShowLot packing order {body.PackingOrderNumber} not found." });
        if (order.Status == "Ready to Pack")
            return await Json(req, new { success = false, notStarted = true, message = $"Packing has not been started for order {order.PackingOrderNumber} — press Start Packing in the system first." });
        if (order.Shipment == null || DoneStatuses.Contains(order.Shipment.Status))
            return await Json(req, new { success = false, message = $"Shipment for order {order.PackingOrderNumber} has already left." });

        // Every scanned showlot must belong to this order and be unpacked.
        var requested = body.ShowLotBoxNumbers.Distinct().ToList();
        var lines = order.Lines.Where(l => requested.Contains(l.BoxNumber)).ToList();
        var unknown = requested.Except(lines.Select(l => l.BoxNumber)).ToList();
        if (unknown.Count > 0)
            return await Json(req, new { success = false, message = $"Not on order {order.PackingOrderNumber}: box(es) {string.Join(", ", unknown)}." });
        var alreadyPacked = lines.Where(l => l.PackedBoxId != null).Select(l => l.BoxNumber).ToList();
        if (alreadyPacked.Count > 0)
            return await Json(req, new { success = false, message = $"Already packed: box(es) {string.Join(", ", alreadyPacked)}." });

        // Same construction as the web PackShowLots: tare parameter + dimensions per box type.
        var tareParam = await _db.Set<SystemParameter>().FirstOrDefaultAsync(p => p.Key == $"BoxTareWeight_{body.BoxType}");
        var tareWeight = tareParam != null && decimal.TryParse(tareParam.Value, out var tw) ? tw : 0m;
        var dimensions = await _db.Set<BoxTypeDimension>().FirstOrDefaultAsync(d => d.BoxType == body.BoxType);
        var netWeight = body.GrossWeight - tareWeight;

        // Scanner flow has no separate approve step — the operator closed, weighed and placed the
        // box at the OUT location, so it's final ("Approved") immediately.
        var packedBox = new PackedBox
        {
            PackingOrderId = order.Id,
            BoxNumber = body.BoxNumber,
            BoxType = body.BoxType,
            GrossWeight = body.GrossWeight,
            NetWeight = netWeight > 0 ? netWeight : body.GrossWeight,
            TareWeight = tareWeight,
            Weight = body.GrossWeight,
            HeightM = dimensions?.HeightM ?? 0,
            WidthM = dimensions?.WidthM ?? 0,
            LengthM = dimensions?.LengthM ?? 0,
            Status = "Approved"
        };
        _db.PackedBoxes.Add(packedBox);
        await _db.SaveChangesAsync();

        foreach (var line in lines)
        {
            line.PackedBoxId = packedBox.Id;
            line.MovedToOutAt = DateTime.UtcNow;   // the packed box goes straight to the OUT location

            // Persist the physical work OUTSIDE the shipment: which carton this showlot sits in and
            // where — survives shipment deletion and is re-applied on recreate.
            var state = await _db.BoxPhysicalStates.FindAsync(line.BoxNumber);
            if (state == null)
            {
                state = new BoxPhysicalState { BoxNumber = line.BoxNumber };
                _db.BoxPhysicalStates.Add(state);
            }
            state.OutLocation = order.Shipment.OutLocation;
            state.MovedAt = DateTime.UtcNow;
            state.PackedBoxNumber = body.BoxNumber;
            state.PackedBoxType = body.BoxType;
            state.PackedGrossWeight = body.GrossWeight;
            state.UpdatedAt = DateTime.UtcNow;
        }

        // Last showlot packed -> order completes; the shipment moves on from ShowLot Packing.
        var remaining = order.Lines.Count(l => l.PackedBoxId == null);
        var orderCompleted = false;
        if (remaining == 0 && order.Status != "Packed")
        {
            order.Status = "Packed";
            if (order.Shipment.Status == "ShowLot Packing")
                order.Shipment.Status = "Pending";
            orderCompleted = true;
        }
        await _db.SaveChangesAsync();

        // Everything packed and staged turns the whole SHIPMENT Ready and regenerates its shipping
        // documents (checked on every close — cheap no-op until the shipment is actually complete).
        var shipmentReady = await _readyService.TryCompleteAsync(order.ShipmentId);

        var outLoc = order.Shipment.OutLocation;
        return await Json(req, new
        {
            success = true,
            packedBoxId = packedBox.Id,
            boxNumber = packedBox.BoxNumber,
            boxType = packedBox.BoxType,
            grossWeight = packedBox.GrossWeight,
            netWeight = packedBox.NetWeight,
            showLotCount = lines.Count,
            totalSkins = lines.Sum(l => l.Skins),
            packingOrderNumber = order.PackingOrderNumber,
            outLocation = outLoc,
            remainingShowlots = remaining,
            orderCompleted,
            shipmentReady,
            message = shipmentReady
                ? $"Box {packedBox.BoxNumber} closed ({lines.Count} showlots, {lines.Sum(l => l.Skins)} skins) — put it at {outLoc ?? "?"}. Shipment {order.Shipment.ShipmentNumber} is READY (documents generated)."
                : orderCompleted
                    ? $"Box {packedBox.BoxNumber} closed ({lines.Count} showlots, {lines.Sum(l => l.Skins)} skins) — put it at {outLoc ?? "?"}. Order {order.PackingOrderNumber} COMPLETE."
                    : $"Box {packedBox.BoxNumber} closed ({lines.Count} showlots, {lines.Sum(l => l.Skins)} skins) — put it at {outLoc ?? "?"}. {remaining} showlot(s) left to pack."
        });
    }

    // Work list: In Production ShowLot orders on in-house shipments, plus the available box types.
    private async Task<HttpResponseData> ListOrdersAsync(HttpRequestData req)
    {
        var orders = await _db.PackingOrders
            .Where(p => p.Type == "ShowLot" && p.Status == "In Production"
                        && p.Shipment != null && !DoneStatuses.Contains(p.Shipment.Status))
            .OrderBy(p => p.Shipment!.CreatedAt).ThenBy(p => p.PackingOrderNumber)
            .Select(p => new
            {
                p.PackingOrderNumber,
                p.Status,
                ShipmentNumber = p.Shipment!.ShipmentNumber,
                OutLocation = p.Shipment.OutLocation,
                BuyerNumber = p.Shipment.Buyer != null ? p.Shipment.Buyer.BuyerNumber : "",
                BuyerName = p.Shipment.Buyer != null ? p.Shipment.Buyer.Name : "",
                TotalShowlots = p.Lines.Count,
                PackedShowlots = p.Lines.Count(l => l.PackedBoxId != null),
                Showlots = p.Lines.OrderBy(l => l.BoxNumber).Select(l => new
                {
                    l.BoxNumber,
                    l.LotNumber,
                    l.Skins,
                    Packed = l.PackedBoxId != null,
                    PackedBoxNumber = l.PackedBox != null ? l.PackedBox.BoxNumber : null
                }).ToList()
            })
            .ToListAsync();

        var boxTypes = await _db.Set<BoxTypeDimension>()
            .Select(d => new { d.BoxType, d.WeightKg, d.LengthM, d.WidthM, d.HeightM })
            .ToListAsync();

        return await Json(req, new { orders, boxTypes });
    }

    private async Task<int?> ResolveBoxNumberAsync(System.Collections.Specialized.NameValueCollection query)
    {
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

    private async Task<ShowlotLine?> FindShowlotLineAsync(int boxNumber)
    {
        return await _db.PackingOrderLines
            .Where(l => l.BoxNumber == boxNumber
                        && l.PackingOrder!.Type == "ShowLot"
                        && l.PackingOrder.Shipment != null
                        && !DoneStatuses.Contains(l.PackingOrder.Shipment.Status))
            .OrderByDescending(l => l.Id)
            .Select(l => new ShowlotLine
            {
                BoxNumber = l.BoxNumber,
                LotNumber = l.LotNumber,
                Skins = l.Skins,
                PackedBoxId = l.PackedBoxId,
                PackedBoxNumber = l.PackedBox != null ? l.PackedBox.BoxNumber : null,
                PackingOrderNumber = l.PackingOrder!.PackingOrderNumber,
                OrderStatus = l.PackingOrder.Status,
                ShipmentNumber = l.PackingOrder.Shipment!.ShipmentNumber,
                OutLocation = l.PackingOrder.Shipment.OutLocation
            })
            .FirstOrDefaultAsync();
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

    public class ClosePackBoxDto
    {
        public string PackingOrderNumber { get; set; } = "";
        public string BoxType { get; set; } = "";
        public string BoxNumber { get; set; } = "";
        public decimal GrossWeight { get; set; }
        public List<int> ShowLotBoxNumbers { get; set; } = new();
    }

    private sealed class ShowlotLine
    {
        public int BoxNumber { get; set; }
        public int LotNumber { get; set; }
        public int Skins { get; set; }
        public int? PackedBoxId { get; set; }
        public string? PackedBoxNumber { get; set; }
        public string PackingOrderNumber { get; set; } = "";
        public string OrderStatus { get; set; } = "";
        public string ShipmentNumber { get; set; } = "";
        public string? OutLocation { get; set; }
    }
}
