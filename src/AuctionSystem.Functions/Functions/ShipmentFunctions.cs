using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;

namespace AuctionSystem.Functions.Functions;

public class ShipmentFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly BlobStorageService? _blobStorage;
    private readonly ShipmentReadyService? _readyService;
    private readonly EmailService? _emailService;
    private readonly ILogger<ShipmentFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ShipmentFunctions(AuctionDbContext db, CatalogDbContext catalogDb, ILogger<ShipmentFunctions> logger, BlobStorageService? blobStorage = null, ShipmentReadyService? readyService = null, EmailService? emailService = null)
    {
        _readyService = readyService;
        _emailService = emailService;
        _db = db;
        _catalogDb = catalogDb;
        _logger = logger;
        _blobStorage = blobStorage;
    }

    [Function("GetShipments")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipments")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var statusFilter = query["status"];
        var buyerFilter = query["buyerId"];

        var q = _db.Shipments
            .Include(s => s.Shipper)
            .Include(s => s.Buyer)
            .Include(s => s.ShippingAddress)
            .Include(s => s.Lines).ThenInclude(l => l.Invoice)
            .AsQueryable();

        if (!string.IsNullOrEmpty(statusFilter))
            q = q.Where(s => s.Status == statusFilter);
        if (int.TryParse(buyerFilter, out var bId))
            q = q.Where(s => s.BuyerId == bId);

        var shipments = await q
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new
            {
                s.Id,
                s.ShipmentNumber,
                ShipperId = s.ShipperId,
                ShipperName = s.Shipper != null ? s.Shipper.Name : "",
                ShipperCode = s.Shipper != null ? s.Shipper.Code : "",
                BuyerId = s.BuyerId,
                BuyerName = s.Buyer != null ? s.Buyer.Name : "",
                BuyerNumber = s.Buyer != null ? s.Buyer.BuyerNumber : "",
                s.ShippingAddressId,
                ShippingAddressSummary = s.ShippingAddress != null
                    ? s.ShippingAddress.Name + " — " + s.ShippingAddress.AddressLine1 + ", " + s.ShippingAddress.City + " " + s.ShippingAddress.Country
                    : "",
                s.TrackingNumber,
                s.Status,
                s.Notes,
                s.OutLocation,
                s.Pallets,
                s.PackingListPdfUrl,
                s.ShippingInvoicePdfUrl,
                s.CertUrl,
                s.CreatedAt,
                s.ShippedAt,
                s.DeliveredAt,
                LotCount = s.Lines.Count,
                BoxCount = _db.PackingOrders.Where(p => p.ShipmentId == s.Id)
                    .SelectMany(p => p.Lines).Select(l => l.BoxNumber).Distinct().Count(),
                // Packing has started (Start Packing pressed on any order) -> the shipment must not
                // be deletable anymore.
                PackingStarted = _db.PackingOrders.Any(p => p.ShipmentId == s.Id && p.Status != "Ready to Pack"),
                Lots = s.Lines.Select(l => new
                {
                    l.LotNumber,
                    l.InvoiceId,
                    InvoiceNumber = l.Invoice != null ? l.Invoice.InvoiceNumber : "",
                    PdfUrl = l.Invoice != null ? l.Invoice.PdfUrl : null,
                    l.Notes
                }).ToList()
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(shipments, JsonOptions));
        return response;
    }

    [Function("GetReleasedLotsForShipment")]
    public async Task<HttpResponseData> GetReleasedLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipments/released-lots")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var buyerFilter = query["buyerId"];

        // Get released invoices (not credit notes)
        var invoiceQuery = _db.Invoices
            .Include(i => i.Buyer)
            .Include(i => i.Broker)
            .Include(i => i.Lines)
            .Where(i => (i.ShippingStatus == "Released" || i.Status == InvoiceStatus.ReleasedToShip || i.Status == InvoiceStatus.Packing) && !i.IsCreditNote);

        if (int.TryParse(buyerFilter, out var bId))
            invoiceQuery = invoiceQuery.Where(i => i.BuyerId == bId);

        var invoices = await invoiceQuery.ToListAsync();

        // Get lot numbers already in a shipment
        var shippedLotNumbers = await _db.ShipmentLines.Select(l => l.LotNumber).Distinct().ToListAsync();

        // Build lot list from invoice lines, excluding already-shipped lots and credited lots
        var creditNotes = await _db.Invoices
            .Include(i => i.Lines)
            .Where(i => i.IsCreditNote && i.OriginalInvoiceId != null)
            .ToListAsync();
        var creditedLotsByInvoice = creditNotes
            .GroupBy(cn => cn.OriginalInvoiceId!.Value)
            .ToDictionary(g => g.Key, g => g.SelectMany(cn => cn.Lines.Select(l => l.LotNumber)).Distinct().ToHashSet());

        var lots = new List<object>();
        foreach (var inv in invoices)
        {
            var creditedLots = creditedLotsByInvoice.GetValueOrDefault(inv.Id) ?? new HashSet<int>();
            foreach (var line in inv.Lines.Where(l => !shippedLotNumbers.Contains(l.LotNumber) && !creditedLots.Contains(l.LotNumber)))
            {
                lots.Add(new
                {
                    line.LotNumber,
                    InvoiceId = inv.Id,
                    inv.InvoiceNumber,
                    inv.BuyerId,
                    BuyerName = inv.Buyer?.Name ?? "",
                    BuyerNumber = inv.Buyer?.BuyerNumber ?? "",
                    BrokerName = inv.Broker?.CompanyName ?? "",
                    line.Skins,
                    line.PricePerSkin,
                    line.HammerPrice
                });
            }
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(lots, JsonOptions));
        return response;
    }

    [Function("CreateShipment")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shipments")] HttpRequestData req)
    {
        try
        {
        var body = await req.ReadFromJsonAsync<CreateShipmentDto>();
        if (body == null || body.ShipperId <= 0 || body.BuyerId <= 0)
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            await bad.WriteStringAsync(JsonSerializer.Serialize(new { error = "ShipperId and BuyerId are required" }, JsonOptions));
            return bad;
        }

        if (body.LotNumbers == null || !body.LotNumbers.Any())
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            await bad.WriteStringAsync(JsonSerializer.Serialize(new { error = "At least one lot is required" }, JsonOptions));
            return bad;
        }

        // Generate shipment number
        var maxNum = await _db.Shipments
            .Where(s => s.ShipmentNumber.StartsWith("SH"))
            .Select(s => s.ShipmentNumber)
            .ToListAsync();
        var nextNum = 1;
        foreach (var n in maxNum)
        {
            if (int.TryParse(n.Replace("SH", ""), out var num) && num >= nextNum)
                nextNum = num + 1;
        }

        // Find invoice IDs for each lot
        var lotInvoiceMap = await _db.Invoices
            .Include(i => i.Lines)
            .Where(i => !i.IsCreditNote && (i.ShippingStatus == "Released" || i.Status == InvoiceStatus.ReleasedToShip || i.Status == InvoiceStatus.Packing))
            .SelectMany(i => i.Lines.Select(l => new { l.LotNumber, InvoiceId = i.Id }))
            .Where(x => body.LotNumbers.Contains(x.LotNumber))
            .ToDictionaryAsync(x => x.LotNumber, x => x.InvoiceId);

        // A lot can only be shipped if it has a released invoice — reject clearly rather than
        // creating a shipment line with an invalid invoice reference.
        var notInvoiced = body.LotNumbers.Where(ln => !lotInvoiceMap.ContainsKey(ln)).ToList();
        if (notInvoiced.Any())
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            await bad.WriteStringAsync(JsonSerializer.Serialize(new
            {
                error = $"These lots have no invoice released for shipping and cannot be shipped: {string.Join(", ", notInvoiced)}"
            }, JsonOptions));
            return bad;
        }

        var shipment = new Shipment
        {
            ShipmentNumber = $"SH{nextNum:D5}",
            ShipperId = body.ShipperId,
            BuyerId = body.BuyerId,
            ShippingAddressId = body.ShippingAddressId,
            TrackingNumber = body.TrackingNumber ?? "",
            Notes = body.Notes ?? "",
            Status = "Packing"
        };

        // Allot the first free outgoing lane (LANE-1..LANE-20) — where every box of this shipment
        // is put. A lane is occupied while its shipment is still in-house and frees up once the
        // shipment is Shipped/Delivered/Cancelled. All 20 taken -> no lane (null).
        var doneStatuses = new[] { "Shipped", "Delivered", "Cancelled" };
        var takenLocations = await _db.Shipments
            .Where(s => s.OutLocation != null && !doneStatuses.Contains(s.Status))
            .Select(s => s.OutLocation!)
            .ToListAsync();
        // Prefer the location where this shipment's boxes ALREADY physically sit (surviving state
        // from a deleted shipment) so a recreate doesn't ask anyone to move boxes.
        string? preferredLocation = null;
        if (body.BoxNumbers != null && body.BoxNumbers.Count > 0)
        {
            preferredLocation = await _db.BoxPhysicalStates
                .Where(s => body.BoxNumbers.Contains(s.BoxNumber) && s.OutLocation != null)
                .GroupBy(s => s.OutLocation)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefaultAsync();
        }
        shipment.OutLocation = preferredLocation != null && !takenLocations.Contains(preferredLocation)
            ? preferredLocation
            : Enumerable.Range(1, 20)
                .Select(i => $"LANE-{i}")
                .FirstOrDefault(loc => !takenLocations.Contains(loc));

        foreach (var lotNumber in body.LotNumbers)
        {
            shipment.Lines.Add(new ShipmentLine
            {
                LotNumber = lotNumber,
                // Guaranteed present — the not-invoiced lots were rejected above.
                InvoiceId = lotInvoiceMap[lotNumber]
            });
        }

        _db.Shipments.Add(shipment);

        // Determine auction number for snapshot tables
        var auctionNumber = await _db.Lots
            .Where(l => body.LotNumbers.Contains(l.LotNumber))
            .Select(l => l.Auction.AuctionNumber)
            .FirstOrDefaultAsync();

        // Try snapshot tables first, fall back to live tables
        var snapshotLotsTable = auctionNumber != null ? $"auction.[{auctionNumber}.Lots]" : null;
        var snapshotBoxesTable = auctionNumber != null ? $"auction.[{auctionNumber}.Boxes]" : null;
        var useSnapshot = false;
        if (snapshotLotsTable != null)
        {
            try
            {
                await _catalogDb.Database.SqlQueryRaw<int>($"SELECT TOP 1 1 AS Value FROM {snapshotLotsTable}").FirstOrDefaultAsync();
                useSnapshot = true;
            }
            catch { useSnapshot = false; }
        }

        // Get catalog lots from snapshot or live table
        List<CatalogLotResult> catalogLots;
        if (useSnapshot)
        {
            catalogLots = await _catalogDb.Database
                .SqlQueryRaw<CatalogLotResult>($"SELECT LotNumber, IncludedBoxNumbers FROM {snapshotLotsTable} WHERE LotNumber IN (" +
                    string.Join(",", body.LotNumbers) + ")")
                .ToListAsync();
        }
        else
        {
            catalogLots = await _catalogDb.CatalogLots
                .Where(cl => body.LotNumbers.Contains(cl.LotNumber))
                .Select(cl => new CatalogLotResult { LotNumber = cl.LotNumber, IncludedBoxNumbers = cl.IncludedBoxNumbers ?? "" })
                .ToListAsync();
        }

        var allBoxNumbers = new List<int>();
        foreach (var cl in catalogLots)
        {
            string? boxNums = cl.IncludedBoxNumbers;
            if (string.IsNullOrEmpty(boxNums)) continue;
            foreach (var boxStr in boxNums.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(boxStr.Trim(), out var n) && n > 0)
                    allBoxNumbers.Add(n);
            }
        }
        allBoxNumbers = allBoxNumbers.Distinct().ToList();

        // Box-level shipping: if specific boxes were selected, pack ONLY those (a subset of the
        // lots' boxes). Everything downstream — packing orders, packing list, show-lot detection,
        // weights — flows from allBoxNumbers, so filtering here scopes the whole shipment.
        if (body.BoxNumbers != null && body.BoxNumbers.Count > 0)
        {
            var requested = body.BoxNumbers.ToHashSet();
            allBoxNumbers = allBoxNumbers.Where(b => requested.Contains(b)).ToList();
        }

        var hasShowLot = false;
        if (allBoxNumbers.Count > 0)
        {
            var boxTable = useSnapshot ? snapshotBoxesTable! : "auction.boxes";
            hasShowLot = await _catalogDb.Database
                .SqlQueryRaw<int>($"SELECT 1 AS Value FROM {boxTable} WHERE BoxNumber IN (" +
                    string.Join(",", allBoxNumbers) + ") AND BoxStatus = 'Showlot'")
                .AnyAsync();
        }

        if (hasShowLot)
            shipment.Status = "ShowLot Packing";

        // Affected invoices — their shipping status is recomputed AFTER the packing orders are
        // saved (below), so a partial (box-level) shipment keeps the invoice shippable.
        var invoiceIds = lotInvoiceMap.Values.Distinct().ToList();

        await _db.SaveChangesAsync();

        // Build box-to-lot mapping
        var boxToLotMap = new Dictionary<int, int>();
        foreach (var cl in catalogLots)
        {
            string? incBoxNums = cl.IncludedBoxNumbers;
            if (string.IsNullOrEmpty(incBoxNums)) continue;
            foreach (var boxStr in incBoxNums.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(boxStr.Trim(), out var bn) && bn > 0)
                    boxToLotMap[bn] = cl.LotNumber;
            }
        }

        // Get all box data and locations from snapshot or live tables
        var allBoxData = new List<BoxViewResult>();
        var boxLocations = new Dictionary<int, string>();
        if (allBoxNumbers.Count > 0)
        {
            if (useSnapshot)
            {
                // Read from snapshot — boxes table already has BoxLocation
                allBoxData = await _catalogDb.Database
                    .SqlQueryRaw<BoxViewResult>($"SELECT BoxNumber, Skins, BoxType, BoxStatus FROM {snapshotBoxesTable} WHERE BoxNumber IN (" +
                        string.Join(",", allBoxNumbers) + ")")
                    .ToListAsync();

                var locations = await _catalogDb.Database
                    .SqlQueryRaw<BoxStagingResult>($"SELECT BoxNumber, ISNULL(BoxWeight, 0) AS BoxWeight, BoxLocation FROM {snapshotBoxesTable} WHERE BoxNumber IN (" +
                        string.Join(",", allBoxNumbers) + ")")
                    .ToListAsync();
                foreach (var s in locations)
                    if (!string.IsNullOrEmpty(s.BoxLocation))
                        boxLocations[s.BoxNumber] = s.BoxLocation;
            }
            else
            {
                allBoxData = await _catalogDb.Database
                    .SqlQueryRaw<BoxViewResult>("SELECT BoxNumber, Skins, BoxType, BoxStatus FROM auction.boxes WHERE BoxNumber IN (" +
                        string.Join(",", allBoxNumbers) + ")")
                    .ToListAsync();

                try
                {
                    var staging = await _catalogDb.Database
                        .SqlQueryRaw<BoxStagingResult>("SELECT CAST(BoxNumber AS INT) AS BoxNumber, Weight AS BoxWeight, BoxLocation FROM dbo.boxstatingfromkphg WHERE BoxNumber IN (" +
                            string.Join(",", allBoxNumbers) + ")")
                        .ToListAsync();
                    foreach (var s in staging)
                        if (!string.IsNullOrEmpty(s.BoxLocation))
                            boxLocations[s.BoxNumber] = s.BoxLocation;
                }
                catch { /* table may not exist */ }
            }
        }

        // Generate packing order number helper
        var maxPoNum = await _db.PackingOrders
            .Where(p => p.PackingOrderNumber.StartsWith("PO"))
            .Select(p => p.PackingOrderNumber)
            .ToListAsync();
        var nextPoNum = 1;
        foreach (var n in maxPoNum)
        {
            if (int.TryParse(n.Replace("PO", ""), out var num) && num >= nextPoNum)
                nextPoNum = num + 1;
        }

        // Helper to add boxes to a packing order
        void AddBoxesToPackingOrder(PackingOrder po, List<BoxViewResult> boxes)
        {
            foreach (var box in boxes)
            {
                po.Lines.Add(new PackingOrderLine
                {
                    BoxNumber = box.BoxNumber,
                    LotNumber = boxToLotMap.GetValueOrDefault(box.BoxNumber),
                    Skins = box.Skins,
                    BoxType = box.BoxType,
                    Location = boxLocations.GetValueOrDefault(box.BoxNumber, "")
                });
            }
        }

        var showLotBoxes = allBoxData.Where(b => b.BoxStatus.Equals("Showlot", StringComparison.OrdinalIgnoreCase)).ToList();
        var nonShowLotBoxes = allBoxData.Where(b => !b.BoxStatus.Equals("Showlot", StringComparison.OrdinalIgnoreCase)).ToList();

        // Auto-create ShowLot packing order (showlot boxes only)
        if (showLotBoxes.Count > 0)
        {
            var packingOrder = new PackingOrder
            {
                PackingOrderNumber = $"PO{nextPoNum:D5}",
                ShipmentId = shipment.Id,
                Status = "Ready to Pack",
                Type = "ShowLot"
            };
            AddBoxesToPackingOrder(packingOrder, showLotBoxes);

            _db.PackingOrders.Add(packingOrder);
            await _db.SaveChangesAsync();
            nextPoNum++;

            // Generate XML and push to blob storage
            if (_blobStorage != null)
            {
                try
                {
                    var xml = GeneratePackingOrderXml(packingOrder, shipment);
                    await _blobStorage.UploadPackingOrderXmlAsync(packingOrder.PackingOrderNumber, xml);
                    _logger.LogInformation("Packing order XML {Number} uploaded to blob storage", packingOrder.PackingOrderNumber);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to upload packing order XML {Number}", packingOrder.PackingOrderNumber);
                }
            }
        }

        // Auto-create Packing order (non-showlot boxes only)
        if (nonShowLotBoxes.Count > 0)
        {
            var packingOrder = new PackingOrder
            {
                PackingOrderNumber = $"PO{nextPoNum:D5}",
                ShipmentId = shipment.Id,
                Status = "Ready to Pack",
                Type = "Packing"
            };
            AddBoxesToPackingOrder(packingOrder, nonShowLotBoxes);

            _db.PackingOrders.Add(packingOrder);
            await _db.SaveChangesAsync();

            // Generate XML and push to blob storage
            if (_blobStorage != null)
            {
                try
                {
                    var xml = GeneratePackingOrderXml(packingOrder, shipment);
                    await _blobStorage.UploadPackingOrderXmlAsync(packingOrder.PackingOrderNumber, xml);
                    _logger.LogInformation("Packing order XML {Number} uploaded to blob storage", packingOrder.PackingOrderNumber);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to upload packing order XML {Number}", packingOrder.PackingOrderNumber);
                }
            }
        }

        // Now that this shipment's packing orders are persisted, recompute each affected invoice's
        // shipping status: InShipment only when ALL its (uncredited) boxes are shipped, otherwise
        // leave it Released so the remaining boxes stay shippable.
        await RefreshInvoiceShippingStatusAsync(invoiceIds, useSnapshot ? snapshotLotsTable : null);

        // Re-apply surviving physical work (boxes already at an OUT location, showlots already in
        // packed cartons) — deleting a shipment must not undo what happened on the floor.
        await RehydratePhysicalStateAsync(shipment);
        if (_readyService != null)
            await _readyService.TryCompleteAsync(shipment.Id);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, id = shipment.Id, shipmentNumber = shipment.ShipmentNumber, outLocation = shipment.OutLocation }, JsonOptions));
        return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateShipment failed");
            var err = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            err.Headers.Add("Content-Type", "application/json");
            await err.WriteStringAsync(JsonSerializer.Serialize(new { error = ex.Message, stack = ex.StackTrace?.Substring(0, Math.Min(ex.StackTrace.Length, 500)) }, JsonOptions));
            return err;
        }
    }

    // Recompute shipping status for the given invoices: an invoice becomes "InShipment" only when
    // every one of its uncredited boxes is on a shipment's packing order; otherwise it stays
    // "Released" so its still-unshipped boxes remain selectable for a later shipment.
    private async Task RefreshInvoiceShippingStatusAsync(List<int> invoiceIds, string? snapshotLotsTable)
    {
        if (invoiceIds.Count == 0) return;

        var invoices = await _db.Invoices.Include(i => i.Lines)
            .Where(i => invoiceIds.Contains(i.Id)).ToListAsync();

        // Credited lots never ship — exclude their boxes from the "fully shipped" test.
        var creditNotes = await _db.Invoices.Include(i => i.Lines)
            .Where(i => i.IsCreditNote && i.OriginalInvoiceId != null && invoiceIds.Contains(i.OriginalInvoiceId.Value))
            .ToListAsync();
        var creditedByInvoice = creditNotes
            .GroupBy(cn => cn.OriginalInvoiceId!.Value)
            .ToDictionary(g => g.Key, g => g.SelectMany(cn => cn.Lines.Select(l => l.LotNumber)).ToHashSet());

        var lotNumbers = invoices
            .SelectMany(i => i.Lines
                .Where(l => !(creditedByInvoice.GetValueOrDefault(i.Id)?.Contains(l.LotNumber) ?? false))
                .Select(l => l.LotNumber))
            .Distinct().ToList();
        if (lotNumbers.Count == 0) return;

        // Boxes per lot — from the auction snapshot if present, otherwise the live catalog.
        List<CatalogLotResult> catalogLots;
        if (snapshotLotsTable != null)
            catalogLots = await _catalogDb.Database
                .SqlQueryRaw<CatalogLotResult>($"SELECT LotNumber, IncludedBoxNumbers FROM {snapshotLotsTable} WHERE LotNumber IN (" +
                    string.Join(",", lotNumbers) + ")")
                .ToListAsync();
        else
            catalogLots = await _catalogDb.CatalogLots
                .Where(cl => lotNumbers.Contains(cl.LotNumber))
                .Select(cl => new CatalogLotResult { LotNumber = cl.LotNumber, IncludedBoxNumbers = cl.IncludedBoxNumbers ?? "" })
                .ToListAsync();
        var boxesByLot = catalogLots.ToDictionary(c => c.LotNumber, c => ParseBoxNumbers(c.IncludedBoxNumbers));

        // Every box currently on a shipment's packing order (includes the shipment just created).
        var shippedBoxes = (await _db.Set<PackingOrderLine>().Select(l => l.BoxNumber).ToListAsync()).ToHashSet();

        foreach (var inv in invoices)
        {
            var credited = creditedByInvoice.GetValueOrDefault(inv.Id) ?? new HashSet<int>();
            var invBoxes = inv.Lines
                .Where(l => !credited.Contains(l.LotNumber))
                .SelectMany(l => boxesByLot.GetValueOrDefault(l.LotNumber) ?? new List<int>())
                .Distinct().ToList();

            inv.ShippingStatus = invBoxes.Count > 0 && invBoxes.All(b => shippedBoxes.Contains(b))
                ? "InShipment"
                : "Released";
        }
        await _db.SaveChangesAsync();
    }

    private static List<int> ParseBoxNumbers(string? included)
    {
        var list = new List<int>();
        if (string.IsNullOrEmpty(included)) return list;
        foreach (var s in included.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(s.Trim(), out var n) && n > 0) list.Add(n);
        return list;
    }

    private static string GeneratePackingOrderXml(PackingOrder packingOrder, Shipment shipment)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("PackingOrder",
                new XElement("PackingOrderNumber", packingOrder.PackingOrderNumber),
                new XElement("ShipmentNumber", shipment.ShipmentNumber),
                new XElement("Status", packingOrder.Status),
                new XElement("CreatedAt", packingOrder.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")),
                new XElement("Buyer",
                    new XElement("Name", shipment.Buyer?.Name ?? ""),
                    new XElement("BuyerNumber", shipment.Buyer?.BuyerNumber ?? "")
                ),
                new XElement("Shipper",
                    new XElement("Name", shipment.Shipper?.Name ?? "")
                ),
                new XElement("Type", packingOrder.Type),
                new XElement("Boxes",
                    new XAttribute("Count", packingOrder.Lines.Count),
                    packingOrder.Lines.Select(line =>
                        new XElement("Box",
                            new XElement("BoxNumber", line.BoxNumber),
                            new XElement("LotNumber", line.LotNumber),
                            new XElement("Skins", line.Skins),
                            new XElement("BoxType", line.BoxType),
                            new XElement("Location", line.Location)
                        )
                    )
                )
            )
        );
        return doc.ToString();
    }

    // Re-applies surviving BoxPhysicalState to a freshly created shipment's packing orders: storage
    // boxes already confirmed at an OUT location get their MovedToOutAt back; showlots already in a
    // packed carton get the carton (PackedBox) recreated. Order statuses follow the restored work.
    private async Task RehydratePhysicalStateAsync(Shipment shipment)
    {
        var orders = await _db.PackingOrders
            .Include(p => p.Lines)
            .Where(p => p.ShipmentId == shipment.Id)
            .ToListAsync();
        if (orders.Count == 0) return;

        var boxNumbers = orders.SelectMany(o => o.Lines).Select(l => l.BoxNumber).ToList();
        var states = await _db.BoxPhysicalStates.Where(s => boxNumbers.Contains(s.BoxNumber)).ToListAsync();
        if (states.Count == 0) return;
        var stateByBox = states.ToDictionary(s => s.BoxNumber);

        foreach (var order in orders)
        {
            if (order.Type == "Packing")
            {
                foreach (var line in order.Lines)
                    if (stateByBox.TryGetValue(line.BoxNumber, out var st) && st.MovedAt != null)
                        line.MovedToOutAt = st.MovedAt;
                var moved = order.Lines.Count(l => l.MovedToOutAt != null);
                if (moved == order.Lines.Count) order.Status = "Packed";
                else if (moved > 0) order.Status = "In Production";
            }
            else
            {
                // Rebuild the packed cartons from the surviving per-showlot state.
                var packedGroups = order.Lines
                    .Where(l => stateByBox.TryGetValue(l.BoxNumber, out var st) && !string.IsNullOrEmpty(st.PackedBoxNumber))
                    .GroupBy(l => stateByBox[l.BoxNumber].PackedBoxNumber!)
                    .ToList();
                foreach (var grp in packedGroups)
                {
                    var st = stateByBox[grp.First().BoxNumber];
                    var dims = await _db.Set<BoxTypeDimension>().FirstOrDefaultAsync(d => d.BoxType == st.PackedBoxType);
                    var tareParam = await _db.Set<SystemParameter>().FirstOrDefaultAsync(p => p.Key == $"BoxTareWeight_{st.PackedBoxType}");
                    var tare = tareParam != null && decimal.TryParse(tareParam.Value, out var tw) ? tw : 0m;
                    var gross = st.PackedGrossWeight ?? 0m;
                    var packedBox = new PackedBox
                    {
                        PackingOrderId = order.Id,
                        BoxNumber = st.PackedBoxNumber!,
                        BoxType = st.PackedBoxType ?? "",
                        GrossWeight = gross,
                        NetWeight = gross - tare > 0 ? gross - tare : gross,
                        TareWeight = tare,
                        Weight = gross,
                        HeightM = dims?.HeightM ?? 0,
                        WidthM = dims?.WidthM ?? 0,
                        LengthM = dims?.LengthM ?? 0,
                        Status = "Approved"
                    };
                    _db.PackedBoxes.Add(packedBox);
                    await _db.SaveChangesAsync();
                    foreach (var line in grp)
                    {
                        line.PackedBoxId = packedBox.Id;
                        line.MovedToOutAt = stateByBox[line.BoxNumber].MovedAt ?? DateTime.UtcNow;
                    }
                }
                var packed = order.Lines.Count(l => l.PackedBoxId != null);
                if (packed == order.Lines.Count) order.Status = "Packed";
                else if (packed > 0) order.Status = "In Production";
            }
        }
        await _db.SaveChangesAsync();
        _logger.LogInformation("Rehydrated physical state onto shipment {Number}: {Count} boxes had surviving state",
            shipment.ShipmentNumber, states.Count);
    }

    [Function("UpdateShipmentStatus")]
    public async Task<HttpResponseData> UpdateStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "shipments/{id:int}/status")] HttpRequestData req,
        int id)
    {
        var body = await req.ReadFromJsonAsync<UpdateShipmentStatusDto>();
        if (body == null || string.IsNullOrWhiteSpace(body.Status))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var shipment = await _db.Shipments
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (shipment == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var validStatuses = new[] { "Pending", "Packing", "ShowLot Packing", "Ready", "Ready for courier", "Shipped", "Delivered", "Cancelled" };
        if (!validStatuses.Contains(body.Status))
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            await bad.WriteStringAsync(JsonSerializer.Serialize(new { error = $"Invalid status. Must be one of: {string.Join(", ", validStatuses)}" }, JsonOptions));
            return bad;
        }

        shipment.Status = body.Status;
        if (!string.IsNullOrWhiteSpace(body.TrackingNumber))
            shipment.TrackingNumber = body.TrackingNumber;

        if (body.Status == "Shipped")
        {
            shipment.ShippedAt = DateTime.UtcNow;
            // The boxes have left the building — their physical staging/packing state is history.
            var shippedBoxNumbers = await _db.PackingOrders
                .Where(p => p.ShipmentId == id)
                .SelectMany(p => p.Lines)
                .Select(l => l.BoxNumber)
                .ToListAsync();
            var states = await _db.BoxPhysicalStates.Where(s => shippedBoxNumbers.Contains(s.BoxNumber)).ToListAsync();
            _db.BoxPhysicalStates.RemoveRange(states);
        }
        else if (body.Status == "Delivered")
            shipment.DeliveredAt = DateTime.UtcNow;

        // Update invoice shipping statuses
        var invoiceIds = shipment.Lines.Where(l => l.InvoiceId.HasValue).Select(l => l.InvoiceId!.Value).Distinct().ToList();
        var invoices = await _db.Invoices.Where(i => invoiceIds.Contains(i.Id)).ToListAsync();
        foreach (var inv in invoices)
        {
            if (body.Status == "Shipped")
                inv.ShippingStatus = "Shipped";
            else if (body.Status == "Delivered")
                inv.ShippingStatus = "Delivered";
            else if (body.Status == "Cancelled")
                inv.ShippingStatus = "Released";
        }

        await _db.SaveChangesAsync();

        // Ready for courier = the office confirmed the ship: documents are (re)generated and emailed
        // now; the scanner then confirms the truck loading, which flips the shipment to Shipped.
        // Email trouble never blocks the transition — it comes back as a warning.
        string? emailedTo = null, emailWarning = null;
        if (body.Status == "Ready for courier")
            (emailedTo, emailWarning) = await EmailShippingDocumentsAsync(shipment);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, emailedTo, emailWarning }, JsonOptions));
        return response;
    }

    private async Task<(string? EmailedTo, string? Warning)> EmailShippingDocumentsAsync(Shipment shipment)
    {
        try
        {
            var toParam = await _db.Set<SystemParameter>().FirstOrDefaultAsync(p => p.Key == "ShippingDocsEmail");
            var to = toParam?.Value?.Trim();
            if (string.IsNullOrEmpty(to))
                return (null, "No email sent — the ShippingDocsEmail parameter is empty.");
            if (_emailService == null || !_emailService.IsConfigured)
                return (null, "No email sent — SMTP is not configured (SMTP_HOST app setting).");

            var packingList = await GenerateAndStorePackingListPdfAsync(shipment.Id, isShippingInvoice: false);
            var shippingInvoice = await GenerateAndStorePackingListPdfAsync(shipment.Id, isShippingInvoice: true);
            if (packingList == null || shippingInvoice == null)
                return (null, "No email sent — document generation failed.");

            await _emailService.SendAsync(
                to,
                $"Shipment {shipment.ShipmentNumber} — shipping documents",
                $"Shipment {shipment.ShipmentNumber} has been shipped.\n\nAttached: packing list ({shipment.ShipmentNumber}-PL) and shipping invoice ({shipment.ShipmentNumber}-SI).",
                ($"{shipment.ShipmentNumber}-PL.pdf", packingList),
                ($"{shipment.ShipmentNumber}-SI.pdf", shippingInvoice));
            return (to, null);
        }
        catch (Exception ex)
        {
            // Message inlined — the portal Log stream omits exception details from the template.
            _logger.LogError(ex, "Failed to email shipping documents for {Number}: {Error}", shipment.ShipmentNumber, ex.Message);
            return (null, $"Email failed: {ex.Message}");
        }
    }

    [Function("UpdateShipment")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "shipments/{id:int}")] HttpRequestData req,
        int id)
    {
        var body = await req.ReadFromJsonAsync<UpdateShipmentDto>();
        if (body == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var shipment = await _db.Shipments.FindAsync(id);
        if (shipment == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        if (body.ShipperId.HasValue && body.ShipperId.Value > 0)
            shipment.ShipperId = body.ShipperId.Value;
        if (body.ShippingAddressId.HasValue)
            shipment.ShippingAddressId = body.ShippingAddressId;
        if (body.TrackingNumber != null)
            shipment.TrackingNumber = body.TrackingNumber;
        if (body.Notes != null)
            shipment.Notes = body.Notes;
        if (body.Pallets.HasValue)
            shipment.Pallets = body.Pallets.Value > 0 ? body.Pallets.Value : null;

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true }, JsonOptions));
        return response;
    }

    [Function("ClearPackingListPdf")]
    public async Task<HttpResponseData> ClearPackingListPdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "shipments/{id:int}/packing-list-pdf")] HttpRequestData req,
        int id)
    {
        var shipment = await _db.Shipments.FindAsync(id);
        if (shipment == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        shipment.PackingListPdfUrl = null;
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true }, JsonOptions));
        return response;
    }

    [Function("DeleteShipment")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "shipments/{id:int}")] HttpRequestData req,
        int id)
    {
        var shipment = await _db.Shipments
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (shipment == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        if (shipment.Status != "Pending" && shipment.Status != "ShowLot Packing" && shipment.Status != "Packing" && shipment.Status != "Packed")
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            await bad.WriteStringAsync(JsonSerializer.Serialize(new { error = "Can only delete Pending, Packing, ShowLot Packing, or Packed shipments" }, JsonOptions));
            return bad;
        }

        // Release invoices back
        var invoiceIds = shipment.Lines.Where(l => l.InvoiceId.HasValue).Select(l => l.InvoiceId!.Value).Distinct().ToList();
        var invoices = await _db.Invoices.Where(i => invoiceIds.Contains(i.Id)).ToListAsync();
        foreach (var inv in invoices)
        {
            inv.ShippingStatus = "Released";
        }

        _db.Shipments.Remove(shipment);
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true }, JsonOptions));
        return response;
    }

    // Change a box's TYPE from the packing order line. The type is corrected everywhere it is
    // read from: every packing-order line carrying the box and the auction snapshot boxes table
    // (which feeds the shipping documents' dimensions/tare lookups).
    [Function("UpdateBoxType")]
    public async Task<HttpResponseData> UpdateBoxType(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "shipments/packing-orders/lines/{lineId:int}/boxtype")] HttpRequestData req,
        int lineId)
    {
        var body = await req.ReadFromJsonAsync<UpdateBoxTypeDto>();
        if (body == null || string.IsNullOrWhiteSpace(body.BoxType))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var validType = await _db.BoxTypeDimensions.AnyAsync(d => d.BoxType == body.BoxType);
        if (!validType)
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            await bad.WriteStringAsync(JsonSerializer.Serialize(new { error = $"Unknown box type '{body.BoxType}' — define it under parameters first." }, JsonOptions));
            return bad;
        }

        var line = await _db.PackingOrderLines
            .Include(l => l.PackingOrder).ThenInclude(o => o!.Shipment)
            .FirstOrDefaultAsync(l => l.Id == lineId);
        if (line == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // From Ready for courier on, the shipment is locked — only the loading scanner touches it.
        var lockedStatuses = new[] { "Ready for courier", "Shipped", "Delivered", "Cancelled" };
        if (line.PackingOrder?.Shipment != null && lockedStatuses.Contains(line.PackingOrder.Shipment.Status))
        {
            var locked = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            locked.Headers.Add("Content-Type", "application/json");
            await locked.WriteStringAsync(JsonSerializer.Serialize(new { error = $"Shipment {line.PackingOrder.Shipment.ShipmentNumber} is {line.PackingOrder.Shipment.Status} — boxes are read-only." }, JsonOptions));
            return locked;
        }

        // Every line carrying this box, not just the clicked one.
        var lines = await _db.PackingOrderLines.Where(l => l.BoxNumber == line.BoxNumber).ToListAsync();
        foreach (var l in lines)
            l.BoxType = body.BoxType;
        await _db.SaveChangesAsync();

        // The auction snapshot boxes table is what the documents read box types from.
        var auctionNumber = await _db.Lots
            .Where(l => l.LotNumber == line.LotNumber)
            .Select(l => l.Auction.AuctionNumber)
            .FirstOrDefaultAsync();
        if (!string.IsNullOrEmpty(auctionNumber) && System.Text.RegularExpressions.Regex.IsMatch(auctionNumber, "^[A-Za-z0-9]{1,20}$"))
        {
            try
            {
                await _catalogDb.Database.ExecuteSqlRawAsync(
                    $"UPDATE auction.[{auctionNumber}.Boxes] SET BoxType = {{0}} WHERE BoxNumber = {{1}}",
                    body.BoxType, line.BoxNumber);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Snapshot box-type update failed for box {Box} in auction {Num}", line.BoxNumber, auctionNumber);
            }
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true }, JsonOptions));
        return response;
    }

    public class UpdateBoxTypeDto
    {
        public string BoxType { get; set; } = "";
    }

    // Upload a certificate document for the shipment (any file type, base64 body). Replaces an
    // existing certificate; the URL is stored on the shipment for the View Cert button.
    [Function("UploadShipmentCert")]
    public async Task<HttpResponseData> UploadCert(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shipments/{id:int}/cert")] HttpRequestData req,
        int id)
    {
        var body = await req.ReadFromJsonAsync<UploadCertDto>();
        if (body == null || string.IsNullOrWhiteSpace(body.FileName) || string.IsNullOrWhiteSpace(body.ContentBase64))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var shipment = await _db.Shipments.FindAsync(id);
        if (shipment == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        if (_blobStorage == null)
        {
            var err = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Blob storage is not configured.");
            return err;
        }

        byte[] data;
        try { data = Convert.FromBase64String(body.ContentBase64); }
        catch { return req.CreateResponse(System.Net.HttpStatusCode.BadRequest); }
        if (data.Length > 20 * 1024 * 1024)
        {
            var tooBig = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await tooBig.WriteStringAsync("File too large (max 20 MB).");
            return tooBig;
        }

        // Blob name from the shipment + a sanitized file name so re-uploads overwrite predictably.
        var safeName = string.Concat(body.FileName.Split(Path.GetInvalidFileNameChars()));
        var url = await _blobStorage.UploadFileAsync(
            $"certs/{shipment.ShipmentNumber}-{safeName}",
            data,
            string.IsNullOrWhiteSpace(body.ContentType) ? "application/octet-stream" : body.ContentType);

        shipment.CertUrl = url;
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, certUrl = url }, JsonOptions));
        return response;
    }

    public class UploadCertDto
    {
        public string FileName { get; set; } = "";
        public string? ContentType { get; set; }
        public string ContentBase64 { get; set; } = "";
    }

    [Function("GetShipmentPackingList")]
    public async Task<HttpResponseData> GetPackingList(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipments/{id:int}/packing-list")] HttpRequestData req,
        int id)
    {
        var shipment = await _db.Shipments
            .Include(s => s.Shipper)
            .Include(s => s.Buyer)
            .Include(s => s.ShippingAddress)
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (shipment == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // Collect lot numbers directly from shipment lines
        var lotNumbers = shipment.Lines.Select(l => l.LotNumber).Distinct().ToList();

        // Box-level shipping: show only the boxes actually in this shipment's packing orders, not
        // every box of the lots. Empty => legacy shipment with no packing-order lines; show all.
        var shipmentBoxes = (await _db.PackingOrders.Where(p => p.ShipmentId == id)
            .SelectMany(p => p.Lines).Select(l => l.BoxNumber).Distinct().ToListAsync()).ToHashSet();

        // Determine auction number for snapshot tables
        var plAuctionNum = await _db.Lots
            .Where(l => lotNumbers.Contains(l.LotNumber))
            .Select(l => l.Auction.AuctionNumber)
            .FirstOrDefaultAsync();
        var plSnapshotLots = plAuctionNum != null ? $"auction.[{plAuctionNum}.Lots]" : null;
        var plSnapshotBoxes = plAuctionNum != null ? $"auction.[{plAuctionNum}.Boxes]" : null;
        var plUseSnapshot = false;
        if (plSnapshotLots != null)
        {
            try
            {
                await _catalogDb.Database.SqlQueryRaw<int>($"SELECT TOP 1 1 AS Value FROM {plSnapshotLots}").FirstOrDefaultAsync();
                plUseSnapshot = true;
            }
            catch { }
        }

        // Get CatalogLots to resolve box numbers (from snapshot or live)
        List<CatalogLotResult> plCatalogLots;
        if (plUseSnapshot)
        {
            plCatalogLots = await _catalogDb.Database
                .SqlQueryRaw<CatalogLotResult>($"SELECT LotNumber, IncludedBoxNumbers FROM {plSnapshotLots} WHERE LotNumber IN (" +
                    string.Join(",", lotNumbers) + ")")
                .ToListAsync();
        }
        else
        {
            plCatalogLots = await _catalogDb.CatalogLots
                .Where(cl => lotNumbers.Contains(cl.LotNumber))
                .Select(cl => new CatalogLotResult { LotNumber = cl.LotNumber, IncludedBoxNumbers = cl.IncludedBoxNumbers ?? "" })
                .ToListAsync();
        }

        var allBoxNumbers = plCatalogLots
            .Where(cl => !string.IsNullOrEmpty(cl.IncludedBoxNumbers))
            .SelectMany(cl => cl.IncludedBoxNumbers!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0).Where(n => n > 0))
            .Where(n => shipmentBoxes.Count == 0 || shipmentBoxes.Contains(n))
            .Distinct().ToList();

        // Get box info (skins, box type) from snapshot or live
        var boxInfo = new Dictionary<int, (int Skins, string BoxType)>();
        if (allBoxNumbers.Count > 0)
        {
            var boxTable = plUseSnapshot ? plSnapshotBoxes! : "auction.boxes";
            var boxData = await _catalogDb.Database
                .SqlQueryRaw<BoxViewResult>($"SELECT BoxNumber, Skins, BoxType, BoxStatus FROM {boxTable} WHERE BoxNumber IN (" +
                    string.Join(",", allBoxNumbers) + ")")
                .ToListAsync();
            foreach (var b in boxData)
                boxInfo[b.BoxNumber] = (b.Skins, b.BoxType);
        }

        // Get box staging (actual weight + location) from external staging table
        var boxStaging = new Dictionary<int, (decimal? Weight, string? Location)>();
        if (allBoxNumbers.Count > 0)
        {
            try
            {
                var staging = await _catalogDb.Database
                    .SqlQueryRaw<BoxStagingResult>("SELECT CAST(BoxNumber AS INT) AS BoxNumber, Weight AS BoxWeight, BoxLocation FROM dbo.boxstatingfromkphg WHERE BoxNumber IN (" +
                        string.Join(",", allBoxNumbers) + ")")
                    .ToListAsync();
                foreach (var s in staging)
                    boxStaging[s.BoxNumber] = (s.BoxWeight, s.BoxLocation);
            }
            catch { /* table may not exist */ }

            // If snapshot, also get location from snapshot boxes for any boxes not in staging
            if (plUseSnapshot && plSnapshotBoxes != null)
            {
                try
                {
                    var missingBoxes = allBoxNumbers.Where(b => !boxStaging.ContainsKey(b)).ToList();
                    if (missingBoxes.Count > 0)
                    {
                        var snapBoxes = await _catalogDb.Database
                            .SqlQueryRaw<BoxStagingResult>($"SELECT BoxNumber, ISNULL(BoxWeight, 0) AS BoxWeight, BoxLocation FROM {plSnapshotBoxes} WHERE BoxNumber IN (" +
                                string.Join(",", missingBoxes) + ")")
                            .ToListAsync();
                        foreach (var s in snapBoxes)
                            if (!boxStaging.ContainsKey(s.BoxNumber))
                                boxStaging[s.BoxNumber] = (null, s.BoxLocation);
                    }
                }
                catch { }
            }
        }

        // Get box type dimensions
        var dimensions = await _db.BoxTypeDimensions.ToListAsync();
        var dimLookup = dimensions.ToDictionary(d => d.BoxType, d => d);

        // Build packing list
        var packingLines = new List<object>();
        decimal totalGrossWeight = 0, totalNetWeight = 0, totalVolume = 0;
        int totalBoxes = 0, totalSkins = 0;

        foreach (var cl in plCatalogLots)
        {
            if (string.IsNullOrEmpty(cl.IncludedBoxNumbers)) continue;
            foreach (var boxStr in cl.IncludedBoxNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(boxStr.Trim(), out var boxNumber) || boxNumber <= 0) continue;
                if (shipmentBoxes.Count > 0 && !shipmentBoxes.Contains(boxNumber)) continue;
                var bi = boxInfo.GetValueOrDefault(boxNumber);
                var boxType = bi.BoxType ?? "";
                var dim = !string.IsNullOrEmpty(boxType) && dimLookup.TryGetValue(boxType, out var d) ? d : null;
                var stg = boxStaging.GetValueOrDefault(boxNumber);
                var volumeM3 = dim != null ? dim.LengthM * dim.WidthM * dim.HeightM : 0m;
                var grossWeight = stg.Weight ?? (dim?.WeightKg ?? 0m);
                var netWeight = stg.Weight != null && dim?.WeightKg != null ? stg.Weight.Value - dim.WeightKg : grossWeight;

                packingLines.Add(new
                {
                    LotNumber = cl.LotNumber,
                    BoxNumber = boxNumber,
                    BoxType = boxType,
                    Skins = bi.Skins,
                    GrossWeightKg = grossWeight,
                    NetWeightKg = netWeight,
                    VolumeM3 = volumeM3,
                    TareWeightKg = dim?.WeightKg ?? 0m,
                    Location = stg.Location ?? ""
                });
                totalGrossWeight += grossWeight;
                totalNetWeight += netWeight;
                totalVolume += volumeM3;
                totalBoxes++;
                totalSkins += bi.Skins;
            }
        }

        // Replace individual showlot boxes with packed boxes if packing orders exist
        var packingOrders = await _db.PackingOrders
            .Include(p => p.Lines)
            .Where(p => p.ShipmentId == id)
            .ToListAsync();

        var packingOrderIds = packingOrders.Select(p => p.Id).ToList();

        if (packingOrderIds.Count > 0)
        {
            var packedBoxes = await _db.PackedBoxes
                .Include(b => b.ShowLots)
                .Where(b => packingOrderIds.Contains(b.PackingOrderId) && (b.Status == "Approved" || b.Status == "Closed"))
                .ToListAsync();

            if (packedBoxes.Count > 0)
            {
                // Get box numbers that are packed (to remove from packing lines)
                var packedBoxNumbers = packedBoxes
                    .SelectMany(pb => pb.ShowLots.Select(sl => sl.BoxNumber))
                    .ToHashSet();

                // Remove individual showlot lines that are now in packed boxes
                var remainingLines = new List<object>();
                decimal newGross = 0, newNet = 0, newVol = 0;
                int newBoxCount = 0, newSkins = 0;

                foreach (var line in packingLines.Cast<dynamic>())
                {
                    if (!packedBoxNumbers.Contains((int)line.BoxNumber))
                    {
                        remainingLines.Add(line);
                        newGross += (decimal)line.GrossWeightKg;
                        newNet += (decimal)line.NetWeightKg;
                        newVol += (decimal)line.VolumeM3;
                        newBoxCount++;
                        newSkins += (int)line.Skins;
                    }
                }

                // Add packed boxes as new lines
                foreach (var pb in packedBoxes)
                {
                    var vol = pb.LengthM * pb.WidthM * pb.HeightM;
                    var pbSkins = pb.ShowLots.Sum(s => s.Skins);
                    var gross = pb.GrossWeight > 0 ? pb.GrossWeight : pb.Weight;
                    var net = pb.NetWeight > 0 ? pb.NetWeight : gross - pb.TareWeight;
                    remainingLines.Add(new
                    {
                        LotNumber = 0,
                        BoxNumber = !string.IsNullOrEmpty(pb.BoxNumber) ? int.TryParse(pb.BoxNumber, out var bn) ? bn : pb.Id : pb.Id,
                        BoxType = pb.BoxType + " (packed)",
                        BoxLabel = pb.BoxNumber,
                        Skins = pbSkins,
                        GrossWeightKg = gross,
                        NetWeightKg = net,
                        TareWeightKg = pb.TareWeight,
                        VolumeM3 = vol,
                        Location = "",
                        PackedShowLots = pb.ShowLots.Select(sl => new { sl.BoxNumber, sl.LotNumber, sl.Skins, sl.WeightKg }).ToList()
                    });
                    newGross += gross;
                    newNet += net;
                    newVol += vol;
                    newBoxCount++;
                    newSkins += pbSkins;
                }

                packingLines = remainingLines;
                totalGrossWeight = newGross;
                totalNetWeight = newNet;
                totalVolume = newVol;
                totalBoxes = newBoxCount;
                totalSkins = newSkins;
            }
        }

        var result = new
        {
            shipment.ShipmentNumber,
            ShipperName = shipment.Shipper?.Name ?? "",
            ShipperCode = shipment.Shipper?.Code ?? "",
            BuyerName = shipment.Buyer?.Name ?? "",
            BuyerNumber = shipment.Buyer?.BuyerNumber ?? "",
            ShippingAddress = shipment.ShippingAddress != null ? new
            {
                shipment.ShippingAddress.ContactName,
                shipment.ShippingAddress.AddressLine1,
                shipment.ShippingAddress.AddressLine2,
                shipment.ShippingAddress.City,
                shipment.ShippingAddress.PostalCode,
                shipment.ShippingAddress.Country
            } : null,
            shipment.TrackingNumber,
            shipment.Status,
            shipment.CreatedAt,
            Totals = new
            {
                Boxes = totalBoxes,
                Skins = totalSkins,
                GrossWeightKg = totalGrossWeight,
                NetWeightKg = totalNetWeight,
                VolumeM3 = totalVolume
            },
            PackingLines = packingLines
        };

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, JsonOptions));
        return response;
    }

    // Box contents PDF: every skin in each of the shipment's boxes with its farmer's country of
    // origin (customs). GET so the browser can open it directly in a new tab.
    [Function("GenerateBoxSkinsPdf")]
    public async Task<HttpResponseData> GenerateBoxSkinsPdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipments/{id:int}/box-skins-pdf")] HttpRequestData req,
        int id)
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        var shipment = await _db.Shipments
            .Include(s => s.Shipper)
            .Include(s => s.Buyer)
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (shipment == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var lotNumbers = shipment.Lines.Select(l => l.LotNumber).Distinct().ToList();

        // Box-level shipping: only the boxes in this shipment's packing orders; empty => legacy
        // shipment, all lot boxes.
        var shipmentBoxes = (await _db.PackingOrders.Where(p => p.ShipmentId == id)
            .SelectMany(p => p.Lines).Select(l => l.BoxNumber).Distinct().ToListAsync()).ToHashSet();

        var auctionNum = await _db.Lots
            .Where(l => lotNumbers.Contains(l.LotNumber))
            .Select(l => l.Auction.AuctionNumber)
            .FirstOrDefaultAsync();
        var snapLots = auctionNum != null ? $"auction.[{auctionNum}.Lots]" : null;
        var snapSkins = auctionNum != null ? $"auction.[{auctionNum}.Skins]" : null;
        var useSnapshot = false;
        if (snapLots != null)
        {
            try { await _catalogDb.Database.SqlQueryRaw<int>($"SELECT TOP 1 1 AS Value FROM {snapLots}").FirstOrDefaultAsync(); useSnapshot = true; }
            catch { }
        }

        // Box -> lot map from the lots' IncludedBoxNumbers
        List<CatalogLotResult> catLots;
        if (useSnapshot)
        {
            catLots = await _catalogDb.Database
                .SqlQueryRaw<CatalogLotResult>($"SELECT LotNumber, IncludedBoxNumbers FROM {snapLots} WHERE LotNumber IN (" +
                    string.Join(",", lotNumbers) + ")")
                .ToListAsync();
        }
        else
        {
            catLots = await _catalogDb.CatalogLots
                .Where(cl => lotNumbers.Contains(cl.LotNumber))
                .Select(cl => new CatalogLotResult { LotNumber = cl.LotNumber, IncludedBoxNumbers = cl.IncludedBoxNumbers ?? "" })
                .ToListAsync();
        }

        var boxToLot = new Dictionary<int, int>();
        foreach (var cl in catLots)
        {
            if (string.IsNullOrEmpty(cl.IncludedBoxNumbers)) continue;
            foreach (var boxStr in cl.IncludedBoxNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(boxStr.Trim(), out var n) && n > 0 && (shipmentBoxes.Count == 0 || shipmentBoxes.Contains(n)))
                    boxToLot[n] = cl.LotNumber;
        }

        // ?box={n}: one box's contents only (the per-box "Skins PDF" button on the boxes list).
        var boxQuery = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["box"];
        if (int.TryParse(boxQuery, out var singleBox) && singleBox > 0)
            boxToLot = boxToLot.Where(kv => kv.Key == singleBox).ToDictionary(kv => kv.Key, kv => kv.Value);

        if (boxToLot.Count == 0)
        {
            var none = req.CreateResponse(System.Net.HttpStatusCode.NotFound);
            await none.WriteStringAsync("No boxes found for this shipment.");
            return none;
        }

        // Skins per box (snapshot preferred — the auction's frozen truth)
        var skinsTable = useSnapshot ? snapSkins! : "dbo.SkinTable";
        var skins = await _catalogDb.Database
            .SqlQueryRaw<BoxSkinRow>($"SELECT Barcode, BoxNumber, ISNULL(Farmer, '') AS Farmer, farmerGUID AS FarmerGuid FROM {skinsTable} WHERE BoxNumber IN (" +
                string.Join(",", boxToLot.Keys) + ") AND IsActive = 1 ORDER BY BoxNumber, Barcode")
            .ToListAsync();

        // Farmer -> country of origin (stable GUID first, legacy name fallback)
        var farmers = await _db.Farmers
            .Select(f => new { f.FarmerGUID, f.Name, f.Country })
            .ToListAsync();
        var countryByGuid = farmers.Where(f => f.FarmerGUID != null)
            .GroupBy(f => f.FarmerGUID!.Value).ToDictionary(g => g.Key, g => g.First().Country);
        var countryByName = farmers.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Country, StringComparer.OrdinalIgnoreCase);

        string CountryOf(BoxSkinRow s)
        {
            if (s.FarmerGuid.HasValue && countryByGuid.TryGetValue(s.FarmerGuid.Value, out var c) && !string.IsNullOrWhiteSpace(c)) return c;
            if (!string.IsNullOrWhiteSpace(s.Farmer) && countryByName.TryGetValue(s.Farmer.Trim(), out var c2) && !string.IsNullOrWhiteSpace(c2)) return c2;
            return "";
        }

        var skinsByBox = skins.GroupBy(s => s.BoxNumber).OrderBy(g => g.Key).ToList();
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "kopenhagenfur-logo.png");

        var pdfBytes = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(QuestPDF.Helpers.PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(8));

                page.Header().PaddingBottom(10).Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text($"Box Contents — Shipment {shipment.ShipmentNumber}").Bold().FontSize(12);
                        col.Item().Text($"Buyer: {shipment.Buyer?.BuyerNumber} - {shipment.Buyer?.Name}    Shipper: {shipment.Shipper?.Name}").FontSize(8);
                        col.Item().Text($"Date: {DateTime.UtcNow:yyyy-MM-dd}    Boxes: {skinsByBox.Count}    Skins: {skins.Count:N0}").FontSize(8);
                    });
                    if (File.Exists(logoPath))
                        row.ConstantItem(110).Height(40).AlignRight().AlignTop().Image(logoPath, QuestPDF.Infrastructure.ImageScaling.FitArea);
                });

                page.Content().Column(col =>
                {
                    // Overall country-of-origin summary first (the customs answer at a glance)
                    var byCountry = skins.GroupBy(s => string.IsNullOrEmpty(CountryOf(s)) ? "Unknown" : CountryOf(s))
                        .OrderByDescending(g => g.Count()).ToList();
                    col.Item().PaddingBottom(4).Text("Country of origin summary: " +
                        string.Join(", ", byCountry.Select(g => $"{g.Key} {g.Count():N0}"))).Bold().FontSize(9);

                    foreach (var boxGrp in skinsByBox)
                    {
                        var boxCountries = boxGrp.GroupBy(s => string.IsNullOrEmpty(CountryOf(s)) ? "Unknown" : CountryOf(s))
                            .OrderByDescending(g => g.Count()).ToList();

                        col.Item().PaddingTop(8).Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(110);   // Barcode
                                c.RelativeColumn();      // Farmer
                                c.ConstantColumn(120);   // Country of origin
                            });

                            table.Header(header =>
                            {
                                header.Cell().ColumnSpan(3)
                                    .Background(QuestPDF.Helpers.Colors.Grey.Lighten2)
                                    .Border(0.75f).BorderColor(QuestPDF.Helpers.Colors.Grey.Darken1)
                                    .Padding(4)
                                    .Text($"Box {boxGrp.Key} — Lot {boxToLot.GetValueOrDefault(boxGrp.Key)} — {boxGrp.Count():N0} skins — Origin: " +
                                        string.Join(", ", boxCountries.Select(g => $"{g.Key} ({g.Count():N0})")))
                                    .Bold().FontSize(9);

                                foreach (var title in new[] { "Barcode", "Farmer", "Country of Origin" })
                                    header.Cell().Background(QuestPDF.Helpers.Colors.Grey.Lighten3)
                                        .BorderBottom(0.5f).BorderColor(QuestPDF.Helpers.Colors.Grey.Medium)
                                        .Padding(3).Text(title).Bold();
                            });

                            foreach (var s in boxGrp)
                            {
                                table.Cell().BorderBottom(0.25f).BorderColor(QuestPDF.Helpers.Colors.Grey.Lighten1).Padding(3).Text(s.Barcode.ToString());
                                table.Cell().BorderBottom(0.25f).BorderColor(QuestPDF.Helpers.Colors.Grey.Lighten1).Padding(3).Text(s.Farmer);
                                table.Cell().BorderBottom(0.25f).BorderColor(QuestPDF.Helpers.Colors.Grey.Lighten1).Padding(3).Text(string.IsNullOrEmpty(CountryOf(s)) ? "—" : CountryOf(s));
                            }
                        });
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();

        var pdfResp = req.CreateResponse(System.Net.HttpStatusCode.OK);
        pdfResp.Headers.Add("Content-Type", "application/pdf");
        pdfResp.Headers.Add("Content-Disposition", $"inline; filename=\"Box contents - {shipment.ShipmentNumber}.pdf\"");
        await pdfResp.Body.WriteAsync(pdfBytes);
        return pdfResp;
    }

    private class BoxSkinRow
    {
        public long Barcode { get; set; }
        public int BoxNumber { get; set; }
        public string Farmer { get; set; } = "";
        public Guid? FarmerGuid { get; set; }
    }

    [Function("GeneratePackingListPdf")]
    public async Task<HttpResponseData> GeneratePackingListPdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shipments/{id:int}/packing-list-pdf")] HttpRequestData req,
        int id)
    {
        var pdfQuery = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var isShippingInvoice = pdfQuery["type"] == "shipping-invoice";

        var pdfBytes = await GenerateAndStorePackingListPdfAsync(id, isShippingInvoice);
        if (pdfBytes == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var docName = isShippingInvoice ? "Shipping invoice" : "Packing list";
        var pdfResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
        pdfResponse.Headers.Add("Content-Type", "application/pdf");
        pdfResponse.Headers.Add("Content-Disposition", $"attachment; filename=\"{docName}.pdf\"");
        await pdfResponse.Body.WriteAsync(pdfBytes);
        return pdfResponse;
    }

    // Builds the packing list / shipping invoice, uploads it to blob storage (overwriting any stale
    // cached document) and stores the URL on the shipment. Also called by ShipmentReadyService when
    // the shipment turns Ready. Returns null if the shipment doesn't exist.
    public async Task<byte[]?> GenerateAndStorePackingListPdfAsync(int id, bool isShippingInvoice)
    {
        var shipment = await _db.Shipments
            .Include(s => s.Shipper)
            .Include(s => s.Buyer)
            .Include(s => s.ShippingAddress)
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (shipment == null)
            return null;

        // Collect lot numbers from shipment lines
        var pdfLotNumbers = shipment.Lines.Select(l => l.LotNumber).Distinct().ToList();

        // Box-level shipping: restrict the document to the boxes actually in this shipment's
        // packing orders, not every box of the lots. Empty => legacy shipment with no packing-order
        // lines; fall back to all lot boxes.
        var pdfShipmentBoxes = (await _db.PackingOrders.Where(p => p.ShipmentId == id)
            .SelectMany(p => p.Lines).Select(l => l.BoxNumber).Distinct().ToListAsync()).ToHashSet();

        // Lookup HammerPrice per lot (for shipping invoice)
        var pdfLotPrices = new Dictionary<int, decimal>();
        if (isShippingInvoice && pdfLotNumbers.Count > 0)
        {
            var lots = await _db.Lots
                .Where(l => pdfLotNumbers.Contains(l.LotNumber) && l.HammerPrice != null)
                .Select(l => new { l.LotNumber, Price = l.HammerPrice!.Value })
                .ToListAsync();
            foreach (var l in lots)
                pdfLotPrices[l.LotNumber] = l.Price;
        }

        // Determine auction for snapshot
        var pdfAuctionNum = await _db.Lots
            .Where(l => pdfLotNumbers.Contains(l.LotNumber))
            .Select(l => l.Auction.AuctionNumber)
            .FirstOrDefaultAsync();
        var pdfSnapshotBoxes = pdfAuctionNum != null ? $"auction.[{pdfAuctionNum}.Boxes]" : null;
        var pdfUseSnapshot = false;
        if (pdfSnapshotBoxes != null)
        {
            try
            {
                await _catalogDb.Database.SqlQueryRaw<int>($"SELECT TOP 1 1 AS Value FROM {pdfSnapshotBoxes}").FirstOrDefaultAsync();
                pdfUseSnapshot = true;
            }
            catch { }
        }

        // Get catalog lots for box numbers
        var pdfSnapshotLots = pdfAuctionNum != null ? $"auction.[{pdfAuctionNum}.Lots]" : null;
        List<CatalogLotResult> pdfCatalogLots;
        if (pdfUseSnapshot && pdfSnapshotLots != null)
        {
            pdfCatalogLots = await _catalogDb.Database
                .SqlQueryRaw<CatalogLotResult>($"SELECT LotNumber, IncludedBoxNumbers FROM {pdfSnapshotLots} WHERE LotNumber IN (" +
                    string.Join(",", pdfLotNumbers) + ")")
                .ToListAsync();
        }
        else
        {
            pdfCatalogLots = await _catalogDb.CatalogLots
                .Where(cl => pdfLotNumbers.Contains(cl.LotNumber))
                .Select(cl => new CatalogLotResult { LotNumber = cl.LotNumber, IncludedBoxNumbers = cl.IncludedBoxNumbers ?? "" })
                .ToListAsync();
        }

        var pdfAllBoxNumbers = pdfCatalogLots
            .Where(cl => !string.IsNullOrEmpty(cl.IncludedBoxNumbers))
            .SelectMany(cl => cl.IncludedBoxNumbers!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0).Where(n => n > 0))
            .Where(n => pdfShipmentBoxes.Count == 0 || pdfShipmentBoxes.Contains(n))
            .Distinct().ToList();

        // Get box info (skins, type)
        var pdfBoxInfo = new Dictionary<int, (int Skins, string BoxType)>();
        if (pdfAllBoxNumbers.Count > 0)
        {
            var pdfBoxTable = pdfUseSnapshot ? pdfSnapshotBoxes! : "auction.boxes";
            var pdfBoxData = await _catalogDb.Database
                .SqlQueryRaw<BoxViewResult>($"SELECT BoxNumber, Skins, BoxType, BoxStatus FROM {pdfBoxTable} WHERE BoxNumber IN (" +
                    string.Join(",", pdfAllBoxNumbers) + ")")
                .ToListAsync();
            foreach (var b in pdfBoxData)
                pdfBoxInfo[b.BoxNumber] = (b.Skins, b.BoxType);
        }

        // Get box descriptions (SalesType, Group, Gender, Size, etc.)
        var pdfBoxDesc = new Dictionary<int, string>();
        if (pdfAllBoxNumbers.Count > 0)
        {
            try
            {
                var pdfDescTable = pdfUseSnapshot ? pdfSnapshotBoxes! : "auction.boxes";
                var descData = await _catalogDb.Database
                    .SqlQueryRaw<BoxDescriptionResult>($"SELECT BoxNumber, ISNULL(SalesType,'') AS SalesType, ISNULL([Group],'') AS [Group], ISNULL(Gender,'') AS Gender, ISNULL(Size,'') AS Size, ISNULL(HairLength,'') AS HairLength, ISNULL(Color,'') AS Color, ISNULL(Quality,'') AS Quality, ISNULL(Clarity,'') AS Clarity, ISNULL(Damages,'') AS Damages FROM {pdfDescTable} WHERE BoxNumber IN (" +
                        string.Join(",", pdfAllBoxNumbers) + ")")
                    .ToListAsync();
                foreach (var d in descData)
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(d.SalesType)) parts.Add(d.SalesType.Trim());
                    if (!string.IsNullOrWhiteSpace(d.Group)) parts.Add(d.Group.Trim());
                    if (!string.IsNullOrWhiteSpace(d.Gender)) parts.Add(d.Gender.Trim());
                    if (!string.IsNullOrWhiteSpace(d.Size)) parts.Add(d.Size.Trim());
                    if (!string.IsNullOrWhiteSpace(d.Quality)) parts.Add(d.Quality.Trim());
                    if (!string.IsNullOrWhiteSpace(d.Color)) parts.Add(d.Color.Trim());
                    pdfBoxDesc[d.BoxNumber] = string.Join(", ", parts);
                }
            }
            catch { }
        }

        // Get staging (weight)
        var pdfStaging = new Dictionary<int, decimal?>();
        if (pdfAllBoxNumbers.Count > 0)
        {
            try
            {
                var staging = await _catalogDb.Database
                    .SqlQueryRaw<BoxStagingResult>("SELECT CAST(BoxNumber AS INT) AS BoxNumber, Weight AS BoxWeight, BoxLocation FROM dbo.boxstatingfromkphg WHERE BoxNumber IN (" +
                        string.Join(",", pdfAllBoxNumbers) + ")")
                    .ToListAsync();
                foreach (var s in staging)
                    pdfStaging[s.BoxNumber] = s.BoxWeight;
            }
            catch { }

            // Also try snapshot weights
            if (pdfUseSnapshot && pdfSnapshotBoxes != null)
            {
                try
                {
                    var missing = pdfAllBoxNumbers.Where(b => !pdfStaging.ContainsKey(b)).ToList();
                    if (missing.Count > 0)
                    {
                        var snapW = await _catalogDb.Database
                            .SqlQueryRaw<BoxStagingResult>($"SELECT BoxNumber, ISNULL(BoxWeight, 0) AS BoxWeight, BoxLocation FROM {pdfSnapshotBoxes} WHERE BoxNumber IN (" +
                                string.Join(",", missing) + ")")
                            .ToListAsync();
                        foreach (var s in snapW)
                            if (!pdfStaging.ContainsKey(s.BoxNumber))
                                pdfStaging[s.BoxNumber] = s.BoxWeight;
                    }
                }
                catch { }
            }
        }

        // Get box type dimensions
        var pdfDimensions = await _db.BoxTypeDimensions.ToListAsync();
        var pdfDimLookup = pdfDimensions.ToDictionary(d => d.BoxType, d => d);

        // Build packing lines
        var pdfLines = new List<PackingListLine>();
        int pdfTotalSkins = 0, pdfTotalBoxes = 0;
        decimal pdfTotalVol = 0, pdfTotalNet = 0, pdfTotalGross = 0;

        foreach (var cl in pdfCatalogLots)
        {
            if (string.IsNullOrEmpty(cl.IncludedBoxNumbers)) continue;
            foreach (var boxStr in cl.IncludedBoxNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(boxStr.Trim(), out var boxNum) || boxNum <= 0) continue;
                if (pdfShipmentBoxes.Count > 0 && !pdfShipmentBoxes.Contains(boxNum)) continue;
                var bi = pdfBoxInfo.GetValueOrDefault(boxNum);
                var boxType = bi.BoxType ?? "";
                var dim = !string.IsNullOrEmpty(boxType) && pdfDimLookup.TryGetValue(boxType, out var d) ? d : null;
                var vol = dim != null ? dim.LengthM * dim.WidthM * dim.HeightM : 0m;
                var weight = pdfStaging.GetValueOrDefault(boxNum);
                var gross = weight ?? (dim?.WeightKg ?? 0m);
                var net = weight != null && dim?.WeightKg != null ? weight.Value - dim.WeightKg : gross;
                var text = pdfBoxDesc.GetValueOrDefault(boxNum, "");

                pdfLines.Add(new PackingListLine
                {
                    Text = text,
                    LotNo = cl.LotNumber.ToString(),
                    Carton = boxNum.ToString(),
                    Skins = bi.Skins,
                    HammerPrice = pdfLotPrices.GetValueOrDefault(cl.LotNumber),
                    VolumeM3 = vol,
                    NetWeight = net,
                    GrossWeight = gross,
                    BoxType = boxType
                });
                pdfTotalSkins += bi.Skins;
                pdfTotalBoxes++;
                pdfTotalVol += vol;
                pdfTotalNet += net;
                pdfTotalGross += gross;
            }
        }

        // Replace showlots with packed boxes
        var pdfPackingOrders = await _db.PackingOrders
            .Include(p => p.Lines)
            .Where(p => p.ShipmentId == id)
            .ToListAsync();
        var pdfPoIds = pdfPackingOrders.Select(p => p.Id).ToList();
        if (pdfPoIds.Count > 0)
        {
            var pdfPackedBoxes = await _db.PackedBoxes
                .Include(b => b.ShowLots)
                .Where(b => pdfPoIds.Contains(b.PackingOrderId) && (b.Status == "Approved" || b.Status == "Closed"))
                .ToListAsync();
            if (pdfPackedBoxes.Count > 0)
            {
                // Load box type dimensions for tare weight lookup
                var pdfBoxDimTypes = pdfPackedBoxes.Select(pb => pb.BoxType).Distinct().ToList();
                var pdfBoxDimLookup = await _db.Set<BoxTypeDimension>()
                    .Where(d => pdfBoxDimTypes.Contains(d.BoxType))
                    .ToDictionaryAsync(d => d.BoxType, d => d);

                var pdfPackedNums = pdfPackedBoxes
                    .SelectMany(pb => pb.ShowLots.Select(sl => sl.BoxNumber))
                    .ToHashSet();

                var remaining = new List<PackingListLine>();
                int newSkins = 0, newBoxes = 0;
                decimal newVol = 0, newNet = 0, newGross = 0;

                // Keep non-showlot (storage) lines as-is
                foreach (var line in pdfLines)
                {
                    if (!int.TryParse(line.Carton, out var cn) || !pdfPackedNums.Contains(cn))
                    {
                        remaining.Add(line);
                        newSkins += line.Skins;
                        newBoxes++;
                        newVol += line.VolumeM3;
                        newNet += line.NetWeight;
                        newGross += line.GrossWeight;
                    }
                }

                // For each packed box: add individual showlot lines (no weight) + packed box summary line
                foreach (var pb in pdfPackedBoxes)
                {
                    // Individual showlot lines
                    foreach (var sl in pb.ShowLots)
                    {
                        var slDesc = pdfBoxDesc.GetValueOrDefault(sl.BoxNumber, "");
                        var slCatLot = pdfCatalogLots
                            .FirstOrDefault(cl => !string.IsNullOrEmpty(cl.IncludedBoxNumbers) &&
                                cl.IncludedBoxNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .Any(b => int.TryParse(b.Trim(), out var n) && n == sl.BoxNumber));
                        var slLotNo = slCatLot?.LotNumber.ToString() ?? "";
                        var slHammer = slCatLot != null ? pdfLotPrices.GetValueOrDefault(slCatLot.LotNumber) : 0m;
                        remaining.Add(new PackingListLine
                        {
                            Text = slDesc,
                            LotNo = slLotNo,
                            Carton = sl.BoxNumber.ToString(),
                            Skins = sl.Skins,
                            HammerPrice = slHammer,
                            IsShowLot = true
                        });
                    }

                    // Packed box summary line — tare from BoxTypeDimension.WeightKg
                    var pbDim = pdfBoxDimLookup.GetValueOrDefault(pb.BoxType);
                    var tare = pbDim?.WeightKg ?? 0m;
                    var vol = pb.LengthM * pb.WidthM * pb.HeightM;
                    var pbSkins = pb.ShowLots.Sum(s => s.Skins);
                    var gross = pb.GrossWeight > 0 ? pb.GrossWeight : pb.Weight;
                    var net = gross - tare;
                    if (net < 0) net = gross;
                    remaining.Add(new PackingListLine
                    {
                        Carton = pb.BoxNumber,
                        Skins = pbSkins,
                        VolumeM3 = vol,
                        NetWeight = net,
                        GrossWeight = gross,
                        IsPackedBoxSummary = true,
                        BoxType = pb.BoxType
                    });
                    newSkins += pbSkins;
                    newBoxes++;
                    newVol += vol;
                    newNet += net;
                    newGross += gross;
                }

                pdfLines = remaining;
                pdfTotalSkins = newSkins;
                pdfTotalBoxes = newBoxes;
                pdfTotalVol = newVol;
                pdfTotalNet = newNet;
                pdfTotalGross = newGross;
            }
        }

        // Build address info
        var buyerAddrLines = new List<string>();
        if (shipment.Buyer != null)
        {
            if (!string.IsNullOrWhiteSpace(shipment.Buyer.Name2)) buyerAddrLines.Add(shipment.Buyer.Name2);
            if (!string.IsNullOrWhiteSpace(shipment.Buyer.AddressLine1)) buyerAddrLines.Add(shipment.Buyer.AddressLine1);
            if (!string.IsNullOrWhiteSpace(shipment.Buyer.AddressLine2)) buyerAddrLines.Add(shipment.Buyer.AddressLine2);
            var cityLine = string.Join(" ", new[] { shipment.Buyer.PostalCode, shipment.Buyer.City }.Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrEmpty(cityLine)) buyerAddrLines.Add(cityLine);
            if (!string.IsNullOrWhiteSpace(shipment.Buyer.Country)) buyerAddrLines.Add(shipment.Buyer.Country);
        }

        var shipToLines = new List<string>();
        var shipToName = "";
        if (shipment.ShippingAddress != null)
        {
            shipToName = shipment.ShippingAddress.Name ?? shipment.ShippingAddress.ContactName ?? "";
            if (!string.IsNullOrWhiteSpace(shipment.ShippingAddress.AddressLine1)) shipToLines.Add(shipment.ShippingAddress.AddressLine1);
            if (!string.IsNullOrWhiteSpace(shipment.ShippingAddress.AddressLine2)) shipToLines.Add(shipment.ShippingAddress.AddressLine2);
            var shipCityLine = string.Join(" ", new[] { shipment.ShippingAddress.PostalCode, shipment.ShippingAddress.City }.Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrEmpty(shipCityLine)) shipToLines.Add(shipCityLine);
            if (!string.IsNullOrWhiteSpace(shipment.ShippingAddress.Country)) shipToLines.Add(shipment.ShippingAddress.Country);
        }

        var pdfTotalPrice = pdfLines.Sum(l => l.HammerPrice * l.Skins);

        // Carton counts per box type + dimensions (millimetres) for the lines under the grand total.
        // Cartons = storage boxes + packed showlot boxes; showlot detail lines aren't cartons.
        var boxTypeSummaries = pdfLines
            .Where(l => !l.IsShowLot && !string.IsNullOrEmpty(l.BoxType))
            .GroupBy(l => l.BoxType)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var dim = pdfDimLookup.GetValueOrDefault(g.Key);
                var dims = dim != null
                    ? $" ({(int)Math.Round(dim.LengthM * 1000)} x {(int)Math.Round(dim.WidthM * 1000)} x {(int)Math.Round(dim.HeightM * 1000)} mm)"
                    : "";
                return $"{g.Count()} x {g.Key} boxes{dims}";
            })
            .ToList();

        // The shipment's sales invoices, one line each in the header block.
        var pdfInvoiceIds = shipment.Lines.Where(l => l.InvoiceId.HasValue).Select(l => l.InvoiceId!.Value).Distinct().ToList();
        var pdfInvoiceNumbers = await _db.Invoices
            .Where(i => pdfInvoiceIds.Contains(i.Id) && i.InvoiceNumber != "")
            .Select(i => i.InvoiceNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync();

        var pdfData = new PackingListData
        {
            ShipmentNumber = shipment.ShipmentNumber,
            ForwardingAgent = shipment.Shipper?.Name ?? "",
            InvoiceAccount = shipment.Buyer?.BuyerNumber ?? "",
            AwbNumber = shipment.TrackingNumber ?? "",
            Date = shipment.CreatedAt.ToString("dd/MM/yyyy"),
            Destination = shipment.ShippingAddress?.Country ?? "",
            Marking = "",
            SalesInvoiceNumbers = pdfInvoiceNumbers,
            BoxTypeSummaries = boxTypeSummaries,
            BuyerName = shipment.Buyer?.Name ?? "",
            BuyerAddressLines = buyerAddrLines,
            ShipToName = shipToName,
            ShipToAddressLines = shipToLines,
            Lines = pdfLines,
            TotalCartons = pdfTotalBoxes,
            TotalSkins = pdfTotalSkins,
            TotalPrice = pdfTotalPrice,
            TotalVolume = pdfTotalVol,
            TotalNetWeight = pdfTotalNet,
            TotalGrossWeight = pdfTotalGross,
            IsShippingInvoice = isShippingInvoice
        };

        // Generate PDF
        var pdfBytes = PackingListPdfService.GeneratePdf(pdfData);

        // Upload to blob
        if (_blobStorage != null)
        {
            try
            {
                var folder = isShippingInvoice ? "shipping-invoices" : "packing-lists";
                var fileName = $"{folder}/{shipment.ShipmentNumber}.pdf";
                var url = await _blobStorage.UploadPdfAsync(fileName, pdfBytes);
                if (isShippingInvoice)
                    shipment.ShippingInvoicePdfUrl = url;
                else
                    shipment.PackingListPdfUrl = url;
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload {DocType} PDF for {ShipmentNumber}",
                    isShippingInvoice ? "shipping invoice" : "packing list", shipment.ShipmentNumber);
            }
        }

        return pdfBytes;
    }

    [Function("GetPackingOrders")]
    public async Task<HttpResponseData> GetPackingOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipments/packing-orders")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var statusFilter = query["status"];
        var typeFilter = query["type"];

        var poQuery = _db.PackingOrders
            .Include(p => p.Shipment).ThenInclude(s => s!.Buyer)
            .Include(p => p.Shipment).ThenInclude(s => s!.Shipper)
            .Include(p => p.Lines)
            .AsQueryable();

        if (!string.IsNullOrEmpty(statusFilter))
            poQuery = poQuery.Where(p => p.Status == statusFilter);
        if (!string.IsNullOrEmpty(typeFilter))
            poQuery = poQuery.Where(p => p.Type == typeFilter);

        var orders = await poQuery.OrderByDescending(p => p.CreatedAt).ToListAsync();

        var results = orders.Select(po => new
        {
            po.Id,
            po.PackingOrderNumber,
            ShipmentNumber = po.Shipment?.ShipmentNumber ?? "",
            ShipmentId = po.ShipmentId,
            ShipmentStatus = po.Shipment?.Status ?? "",
            BuyerName = po.Shipment?.Buyer?.Name ?? "",
            BuyerNumber = po.Shipment?.Buyer?.BuyerNumber ?? "",
            ShipperName = po.Shipment?.Shipper?.Name ?? "",
            OutLocation = po.Shipment?.OutLocation,
            po.Status,
            po.Type,
            po.CreatedAt,
            BoxCount = po.Lines.Count,
            // A ShowLot order's lines ARE the showlots; storage ("Packing") orders have none.
            ShowLotCount = po.Type == "ShowLot" ? po.Lines.Count : 0,
            Lines = po.Lines.Select(l => new
            {
                l.Id,
                l.BoxNumber,
                l.LotNumber,
                l.Skins,
                l.BoxType,
                l.Location,
                l.PackedBoxId,
                l.MovedToOutAt
            })
        });

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(results, JsonOptions));
        return response;
    }

    [Function("UpdatePackingOrderStatus")]
    public async Task<HttpResponseData> UpdatePackingOrderStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "shipments/packing-orders/{id:int}/status")] HttpRequestData req,
        int id)
    {
        var body = await req.ReadFromJsonAsync<UpdatePackingOrderStatusDto>();
        if (body == null || string.IsNullOrEmpty(body.Status))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var order = await _db.PackingOrders.FindAsync(id);
        if (order == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var validStatuses = new[] { "Ready to Pack", "In Production", "Packed" };
        if (!validStatuses.Contains(body.Status))
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            await bad.WriteStringAsync(JsonSerializer.Serialize(new { error = $"Invalid status. Valid: {string.Join(", ", validStatuses)}" }, JsonOptions));
            return bad;
        }

        order.Status = body.Status;
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true }, JsonOptions));
        return response;
    }

    [Function("DeletePackingOrder")]
    public async Task<HttpResponseData> DeletePackingOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "shipments/packing-orders/{id:int}")] HttpRequestData req,
        int id)
    {
        var order = await _db.PackingOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id);
        if (order == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // Delete XML files from blob storage
        try
        {
            await _blobStorage.DeletePackingOrderXmlsAsync(order.PackingOrderNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete packing order XMLs for {Number}", order.PackingOrderNumber);
        }

        _db.PackingOrders.Remove(order);
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, deletedNumber = order.PackingOrderNumber }, JsonOptions));
        return response;
    }

    [Function("ClearPackingOrderXmls")]
    public async Task<HttpResponseData> ClearPackingOrderXmls(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "shipments/packing-orders/xmls")] HttpRequestData req)
    {
        if (_blobStorage == null)
            return req.CreateResponse(System.Net.HttpStatusCode.ServiceUnavailable);
        var deleted = await _blobStorage.ClearAllPackingOrderXmlsAsync();
        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, deletedFiles = deleted }, JsonOptions));
        return response;
    }

    [Function("GetPackingOrderXmls")]
    public async Task<HttpResponseData> GetPackingOrderXmls(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipments/packing-orders/xmls")] HttpRequestData req)
    {
        if (_blobStorage == null)
        {
            var err = req.CreateResponse(System.Net.HttpStatusCode.ServiceUnavailable);
            err.Headers.Add("Content-Type", "application/json");
            await err.WriteStringAsync(JsonSerializer.Serialize(new { error = "Blob storage not configured" }, JsonOptions));
            return err;
        }

        var xmlFiles = await _blobStorage.ListPackingOrderXmlsAsync();
        var results = new List<object>();

        foreach (var (fileName, folder, content) in xmlFiles)
        {
            try
            {
                var doc = XDocument.Parse(content);
                var root = doc.Root!;
                var boxes = (root.Element("Boxes") ?? root.Element("ShowLotBoxes"))?.Elements("Box").Select(b => new
                {
                    BoxNumber = int.TryParse(b.Element("BoxNumber")?.Value, out var bn) ? bn : 0,
                    LotNumber = int.TryParse(b.Element("LotNumber")?.Value, out var ln) ? ln : 0,
                    Skins = int.TryParse(b.Element("Skins")?.Value, out var sk) ? sk : 0,
                    BoxType = b.Element("BoxType")?.Value ?? "",
                    Location = b.Element("Location")?.Value ?? ""
                }).ToList() ?? new();

                results.Add(new
                {
                    FileName = fileName,
                    Folder = folder,
                    PackingOrderNumber = root.Element("PackingOrderNumber")?.Value ?? "",
                    ShipmentNumber = root.Element("ShipmentNumber")?.Value ?? "",
                    Status = root.Element("Status")?.Value ?? "",
                    Type = root.Element("Type")?.Value ?? "Packing",
                    CreatedAt = root.Element("CreatedAt")?.Value ?? "",
                    BuyerName = root.Element("Buyer")?.Element("Name")?.Value ?? "",
                    BuyerNumber = root.Element("Buyer")?.Element("BuyerNumber")?.Value ?? "",
                    ShipperName = root.Element("Shipper")?.Element("Name")?.Value ?? "",
                    ShowLotCount = boxes.Count,
                    Lines = boxes
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse XML {FileName}", fileName);
                results.Add(new
                {
                    FileName = fileName,
                    Folder = folder,
                    PackingOrderNumber = "",
                    ShipmentNumber = "",
                    Status = "Error",
                    Type = "Unknown",
                    CreatedAt = "",
                    BuyerName = "",
                    BuyerNumber = "",
                    ShipperName = "",
                    ShowLotCount = 0,
                    Lines = new List<object>()
                });
            }
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(results, JsonOptions));
        return response;
    }

    [Function("ConfirmPicking")]
    public async Task<HttpResponseData> ConfirmPicking(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shipments/packing-orders/confirm-picking")] HttpRequestData req)
    {
        if (_blobStorage == null)
            return req.CreateResponse(System.Net.HttpStatusCode.ServiceUnavailable);

        var body = await req.ReadFromJsonAsync<ConfirmPickingDto>();
        if (body == null || string.IsNullOrEmpty(body.PackingOrderNumber))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        try
        {
            // Move XML from new/ to processed/
            var container = await _blobStorage.GetPackingContainerAsync();
            var sourceBlob = container.GetBlobClient($"new/{body.PackingOrderNumber}.xml");
            if (!await sourceBlob.ExistsAsync())
            {
                var notFound = req.CreateResponse(System.Net.HttpStatusCode.NotFound);
                notFound.Headers.Add("Content-Type", "application/json");
                await notFound.WriteStringAsync(JsonSerializer.Serialize(new { error = $"XML file new/{body.PackingOrderNumber}.xml not found" }, JsonOptions));
                return notFound;
            }

            // Read the original XML
            var download = await sourceBlob.DownloadContentAsync();
            var originalXml = download.Value.Content.ToString();

            // Generate response XML with picked confirmation
            var responseXml = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("PickingResponse",
                    new XElement("PackingOrderNumber", body.PackingOrderNumber),
                    new XElement("PickedAt", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")),
                    new XElement("Status", "Picked"),
                    new XElement("PickedBoxes",
                        new XAttribute("Count", body.PickedBoxNumbers?.Count ?? 0),
                        (body.PickedBoxNumbers ?? new()).Select(bn =>
                            new XElement("Box", new XElement("BoxNumber", bn))
                        )
                    )
                )
            );

            // Upload response to processed/ folder
            await _blobStorage.UploadPackingXmlAsync($"processed/{body.PackingOrderNumber}.xml", responseXml.ToString());

            // Delete from new/ folder
            await sourceBlob.DeleteIfExistsAsync();

            // Update packing order status in DB
            var packingOrder = await _db.PackingOrders
                .Include(po => po.Shipment)
                .FirstOrDefaultAsync(po => po.PackingOrderNumber == body.PackingOrderNumber);
            if (packingOrder != null)
            {
                packingOrder.Status = "Picked";
                
                // Check if all packing orders for this shipment are picked
                if (packingOrder.ShipmentId > 0)
                {
                    var allOrders = await _db.PackingOrders
                        .Where(po => po.ShipmentId == packingOrder.ShipmentId)
                        .ToListAsync();
                    var allPicked = allOrders.All(po => po.Id == packingOrder.Id || po.Status == "Picked");
                    if (allPicked && packingOrder.Shipment != null)
                    {
                        packingOrder.Shipment.Status = "Packed";
                    }
                }
                await _db.SaveChangesAsync();
            }

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, packingOrderNumber = body.PackingOrderNumber, pickedBoxes = body.PickedBoxNumbers?.Count ?? 0 }, JsonOptions));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConfirmPicking failed for {PackingOrderNumber}", body.PackingOrderNumber);
            var err = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            err.Headers.Add("Content-Type", "application/json");
            await err.WriteStringAsync(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
            return err;
        }
    }

    [Function("PackShowLots")]
    public async Task<HttpResponseData> PackShowLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shipments/packing-orders/pack-showlots")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<PackShowLotsDto>();
        if (body == null || string.IsNullOrEmpty(body.BoxType) || body.ShowLotLineIds == null || body.ShowLotLineIds.Count == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var order = await _db.PackingOrders
            .Include(o => o.Lines)
            .Include(o => o.Shipment).ThenInclude(s => s!.Buyer)
            .Include(o => o.Shipment).ThenInclude(s => s!.Shipper)
            .FirstOrDefaultAsync(o => o.PackingOrderNumber == body.PackingOrderNumber);
        if (order == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // Get tare weight from system parameters
        var tareKey = $"BoxTareWeight_{body.BoxType}";
        var tareParam = await _db.Set<SystemParameter>().FirstOrDefaultAsync(p => p.Key == tareKey);
        var tareWeight = tareParam != null && decimal.TryParse(tareParam.Value, out var tw) ? tw : 0m;

        var grossWeight = body.GrossWeight > 0 ? body.GrossWeight : body.Weight;
        var netWeight = grossWeight - tareWeight;

        // Get box type dimensions
        var dimensions = await _db.Set<BoxTypeDimension>()
            .FirstOrDefaultAsync(d => d.BoxType == body.BoxType);

        var packedBox = new PackedBox
        {
            PackingOrderId = order.Id,
            BoxNumber = body.BoxNumber,
            BoxType = body.BoxType,
            GrossWeight = grossWeight,
            NetWeight = netWeight > 0 ? netWeight : grossWeight,
            TareWeight = tareWeight,
            Weight = grossWeight,
            HeightM = dimensions?.HeightM ?? 0,
            WidthM = dimensions?.WidthM ?? 0,
            LengthM = dimensions?.LengthM ?? 0,
            Status = "Closed"
        };
        _db.PackedBoxes.Add(packedBox);
        await _db.SaveChangesAsync();

        // Assign selected showlot lines to this packed box + update individual weights
        // ShowLotLineIds contains BoxNumbers from the XML, match against order lines
        var lines = order.Lines.Where(l => body.ShowLotLineIds.Contains(l.BoxNumber)).ToList();
        foreach (var line in lines)
        {
            line.PackedBoxId = packedBox.Id;
            var weightEntry = body.ShowLotWeights.FirstOrDefault(w => w.LineId == line.BoxNumber);
            if (weightEntry != null)
                line.WeightKg = weightEntry.Weight;
        }
        await _db.SaveChangesAsync();

        // Check if all showlots in this packing order are now packed
        var allPacked = order.Lines.All(l => l.PackedBoxId != null);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            success = true,
            packedBoxId = packedBox.Id,
            boxNumber = packedBox.BoxNumber,
            boxType = packedBox.BoxType,
            grossWeight = packedBox.GrossWeight,
            netWeight = packedBox.NetWeight,
            tareWeight = packedBox.TareWeight,
            showLotCount = lines.Count,
            totalSkins = lines.Sum(l => l.Skins),
            allPacked
        }, JsonOptions));
        return response;
    }

    [Function("ApprovePackedBox")]
    public async Task<HttpResponseData> ApprovePackedBox(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shipments/packed-boxes/{packedBoxId:int}/approve")] HttpRequestData req,
        int packedBoxId)
    {
        var packedBox = await _db.PackedBoxes
            .Include(b => b.ShowLots)
            .Include(b => b.PackingOrder).ThenInclude(o => o!.Shipment).ThenInclude(s => s!.Buyer)
            .Include(b => b.PackingOrder).ThenInclude(o => o!.Shipment).ThenInclude(s => s!.Shipper)
            .FirstOrDefaultAsync(b => b.Id == packedBoxId);
        if (packedBox == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        packedBox.Status = "Approved";
        await _db.SaveChangesAsync();

        // Check if all showlot lines are packed — if so, update packing order status
        var order = await _db.PackingOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == packedBox.PackingOrderId);
        if (order != null)
        {
            var allPacked = order.Lines.All(l => l.PackedBoxId != null);
            if (allPacked)
            {
                order.Status = "Packed";

                // Update shipment status from ShowLot Packing to Pending
                var shipment = await _db.Shipments.FindAsync(order.ShipmentId);
                if (shipment != null && shipment.Status == "ShowLot Packing")
                {
                    shipment.Status = "Pending";
                }
                await _db.SaveChangesAsync();
            }
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true }, JsonOptions));
        return response;
    }

    [Function("GetPackedBoxes")]
    public async Task<HttpResponseData> GetPackedBoxes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipments/packing-orders/{packingOrderId:int}/packed-boxes")] HttpRequestData req,
        int packingOrderId)
    {
        var boxes = await _db.PackedBoxes
            .Where(b => b.PackingOrderId == packingOrderId)
            .Include(b => b.ShowLots)
            .OrderBy(b => b.Id)
            .ToListAsync();

        var results = boxes.Select(b => new
        {
            b.Id,
            b.BoxNumber,
            b.BoxType,
            b.Weight,
            b.HeightM,
            b.WidthM,
            b.LengthM,
            b.Status,
            b.CreatedAt,
            ShowLotCount = b.ShowLots.Count,
            TotalSkins = b.ShowLots.Sum(l => l.Skins),
            ShowLots = b.ShowLots.Select(l => new { l.Id, l.BoxNumber, l.LotNumber, l.Skins, l.BoxType })
        });

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(results, JsonOptions));
        return response;
    }

    [Function("CompleteShowLotPacking")]
    public async Task<HttpResponseData> CompleteShowLotPacking(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shipments/packing-orders/complete-showlot")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<CompleteShowLotDto>();
        if (body == null || string.IsNullOrEmpty(body.PackingOrderNumber))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var order = await _db.PackingOrders
            .Include(o => o.Lines)
            .Include(o => o.Shipment)
            .FirstOrDefaultAsync(o => o.PackingOrderNumber == body.PackingOrderNumber);
        if (order == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // Verify all showlots are packed
        var unpackedCount = order.Lines.Count(l => l.PackedBoxId == null);
        if (unpackedCount > 0)
        {
            var badResp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            badResp.Headers.Add("Content-Type", "application/json");
            await badResp.WriteStringAsync(JsonSerializer.Serialize(new { error = $"{unpackedCount} showlot(s) not yet packed" }, JsonOptions));
            return badResp;
        }

        // Get all packed boxes for this order
        var packedBoxes = await _db.PackedBoxes
            .Where(b => b.PackingOrderId == order.Id)
            .Include(b => b.ShowLots)
            .ToListAsync();

        // Update statuses
        order.Status = "Picked";
        if (order.Shipment != null)
        {
            // Check if all packing orders for this shipment are picked
            var allOrders = await _db.PackingOrders
                .Where(po => po.ShipmentId == order.ShipmentId)
                .ToListAsync();
            var allPicked = allOrders.All(po => po.Id == order.Id || po.Status == "Picked");
            if (allPicked)
                order.Shipment.Status = "Packed";
        }
        await _db.SaveChangesAsync();

        // Generate response XML to blob
        if (_blobStorage != null)
        {
            try
            {
                var xml = GenerateShowLotPackingResponseXml(order, packedBoxes);
                await _blobStorage.UploadPackingXmlAsync($"processed/{order.PackingOrderNumber}.xml", xml);

                // Remove from new/
                var container = await _blobStorage.GetPackingContainerAsync();
                var sourceBlob = container.GetBlobClient($"new/{order.PackingOrderNumber}.xml");
                await sourceBlob.DeleteIfExistsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload showlot packing response for {Number}", order.PackingOrderNumber);
            }
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            success = true,
            packingOrderNumber = order.PackingOrderNumber,
            packedBoxCount = packedBoxes.Count,
            totalShowLots = packedBoxes.Sum(b => b.ShowLots.Count)
        }, JsonOptions));
        return response;
    }

    private static string GenerateShowLotPackingResponseXml(PackingOrder order, List<PackedBox> packedBoxes)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("ShowLotPackingResponse",
                new XElement("PackingOrderNumber", order.PackingOrderNumber),
                new XElement("ShipmentNumber", order.Shipment?.ShipmentNumber ?? ""),
                new XElement("CompletedAt", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")),
                new XElement("Status", "Packed"),
                new XElement("PackedBoxes",
                    new XAttribute("Count", packedBoxes.Count),
                    packedBoxes.Select(b =>
                        new XElement("PackedBox",
                            new XElement("BoxNumber", b.BoxNumber),
                            new XElement("BoxType", b.BoxType),
                            new XElement("GrossWeight", b.GrossWeight),
                            new XElement("NetWeight", b.NetWeight),
                            new XElement("TareWeight", b.TareWeight),
                            new XElement("ShowLots",
                                new XAttribute("Count", b.ShowLots.Count),
                                b.ShowLots.Select(l =>
                                    new XElement("ShowLot",
                                        new XElement("BoxNumber", l.BoxNumber),
                                        new XElement("LotNumber", l.LotNumber),
                                        new XElement("Skins", l.Skins),
                                        new XElement("WeightKg", l.WeightKg)
                                    )
                                )
                            )
                        )
                    )
                )
            )
        );
        return doc.ToString();
    }

    private class BoxViewResult
    {
        public int BoxNumber { get; set; }
        public int Skins { get; set; }
        public string BoxType { get; set; } = "";
        public string BoxStatus { get; set; } = "";
    }

    private class BoxDescriptionResult
    {
        public int BoxNumber { get; set; }
        public string SalesType { get; set; } = "";
        public string Group { get; set; } = "";
        public string Gender { get; set; } = "";
        public string Size { get; set; } = "";
        public string HairLength { get; set; } = "";
        public string Color { get; set; } = "";
        public string Quality { get; set; } = "";
        public string Clarity { get; set; } = "";
        public string Damages { get; set; } = "";
    }

    private class BoxStagingResult
    {
        public int BoxNumber { get; set; }
        public decimal? BoxWeight { get; set; }
        public string? BoxLocation { get; set; }
    }

    private class CatalogLotResult
    {
        public int LotNumber { get; set; }
        public string? IncludedBoxNumbers { get; set; }
    }
}

public class CreateShipmentDto
{
    public int ShipperId { get; set; }
    public int BuyerId { get; set; }
    public int? ShippingAddressId { get; set; }
    public string? TrackingNumber { get; set; }
    public string? Notes { get; set; }
    public List<int> LotNumbers { get; set; } = new();
    // Box-level shipping: when supplied, only these boxes are packed/shipped (a subset of the
    // lots' boxes). When null/empty, all of the lots' boxes are shipped (legacy behaviour).
    public List<int>? BoxNumbers { get; set; }
}

public class UpdateShipmentStatusDto
{
    public string Status { get; set; } = "";
    public string? TrackingNumber { get; set; }
}

public class UpdateShipmentDto
{
    public int? ShipperId { get; set; }
    public int? ShippingAddressId { get; set; }
    public string? TrackingNumber { get; set; }
    public string? Notes { get; set; }
    public int? Pallets { get; set; }
}

public class UpdatePackingOrderStatusDto
{
    public string Status { get; set; } = "";
}

public class PackShowLotsDto
{
    public string PackingOrderNumber { get; set; } = "";
    public string BoxType { get; set; } = "";
    public string BoxNumber { get; set; } = "";
    public decimal GrossWeight { get; set; }
    public decimal Weight { get; set; }
    public List<int> ShowLotLineIds { get; set; } = new(); // These are showlot BoxNumbers from XML
    public List<ShowLotWeightEntry> ShowLotWeights { get; set; } = new();
}

public class ShowLotWeightEntry
{
    public int LineId { get; set; }
    public decimal Weight { get; set; }
}

public class ConfirmPickingDto
{
    public string PackingOrderNumber { get; set; } = "";
    public List<int> PickedBoxNumbers { get; set; } = new();
}

public class CompleteShowLotDto
{
    public string PackingOrderNumber { get; set; } = "";
}
