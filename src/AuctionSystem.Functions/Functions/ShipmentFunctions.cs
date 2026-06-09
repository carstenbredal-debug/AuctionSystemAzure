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

namespace AuctionSystem.Functions.Functions;

public class ShipmentFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly BlobStorageService? _blobStorage;
    private readonly ILogger<ShipmentFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ShipmentFunctions(AuctionDbContext db, CatalogDbContext catalogDb, ILogger<ShipmentFunctions> logger, BlobStorageService? blobStorage = null)
    {
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
                    ? s.ShippingAddress.AddressLine1 + ", " + s.ShippingAddress.City + " " + s.ShippingAddress.Country
                    : "",
                s.TrackingNumber,
                s.Status,
                s.Notes,
                s.CreatedAt,
                s.ShippedAt,
                s.DeliveredAt,
                LotCount = s.Lines.Count,
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
            .Where(i => (i.ShippingStatus == "Released" || i.Status == InvoiceStatus.ReleasedToShip) && !i.IsCreditNote);

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
            .Where(i => !i.IsCreditNote && (i.ShippingStatus == "Released" || i.Status == InvoiceStatus.ReleasedToShip))
            .SelectMany(i => i.Lines.Select(l => new { l.LotNumber, InvoiceId = i.Id }))
            .Where(x => body.LotNumbers.Contains(x.LotNumber))
            .ToDictionaryAsync(x => x.LotNumber, x => x.InvoiceId);

        var shipment = new Shipment
        {
            ShipmentNumber = $"SH{nextNum:D5}",
            ShipperId = body.ShipperId,
            BuyerId = body.BuyerId,
            ShippingAddressId = body.ShippingAddressId,
            TrackingNumber = body.TrackingNumber ?? "",
            Notes = body.Notes ?? "",
            Status = "Pending"
        };

        foreach (var lotNumber in body.LotNumbers)
        {
            shipment.Lines.Add(new ShipmentLine
            {
                LotNumber = lotNumber,
                InvoiceId = lotInvoiceMap.GetValueOrDefault(lotNumber)
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
        List<dynamic> catalogLots;
        if (useSnapshot)
        {
            var snapshotLots = await _catalogDb.Database
                .SqlQueryRaw<CatalogLotResult>($"SELECT LotNumber, IncludedBoxNumbers FROM {snapshotLotsTable} WHERE LotNumber IN (" +
                    string.Join(",", body.LotNumbers) + ")")
                .ToListAsync();
            catalogLots = snapshotLots.Select(cl => (dynamic)new { cl.LotNumber, cl.IncludedBoxNumbers }).ToList();
        }
        else
        {
            var liveLots = await _catalogDb.CatalogLots
                .Where(cl => body.LotNumbers.Contains(cl.LotNumber))
                .Select(cl => new { cl.LotNumber, cl.IncludedBoxNumbers })
                .ToListAsync();
            catalogLots = liveLots.Select(cl => (dynamic)new { cl.LotNumber, cl.IncludedBoxNumbers }).ToList();
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

        // Update invoice shipping status for affected invoices
        var invoiceIds = lotInvoiceMap.Values.Distinct().ToList();
        var invoices = await _db.Invoices.Where(i => invoiceIds.Contains(i.Id)).ToListAsync();
        foreach (var inv in invoices)
        {
            inv.ShippingStatus = "InShipment";
        }

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
                    boxToLotMap[bn] = (int)cl.LotNumber;
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
                    .SqlQueryRaw<BoxStagingResult>($"SELECT BoxNumber, 0 AS BoxWeight, BoxLocation FROM {snapshotBoxesTable} WHERE BoxNumber IN (" +
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

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, id = shipment.Id, shipmentNumber = shipment.ShipmentNumber }, JsonOptions));
        return response;
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

        var validStatuses = new[] { "Pending", "ShowLot Packing", "Shipped", "Delivered", "Cancelled" };
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
            shipment.ShippedAt = DateTime.UtcNow;
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

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true }, JsonOptions));
        return response;
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

        if (body.ShippingAddressId.HasValue)
            shipment.ShippingAddressId = body.ShippingAddressId;
        if (body.TrackingNumber != null)
            shipment.TrackingNumber = body.TrackingNumber;
        if (body.Notes != null)
            shipment.Notes = body.Notes;

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

        if (shipment.Status != "Pending" && shipment.Status != "ShowLot Packing")
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            await bad.WriteStringAsync(JsonSerializer.Serialize(new { error = "Can only delete pending or ShowLot Packing shipments" }, JsonOptions));
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

        // Get box staging (actual weight + location) from snapshot or live
        var boxStaging = new Dictionary<int, (decimal? Weight, string? Location)>();
        if (allBoxNumbers.Count > 0)
        {
            try
            {
                if (plUseSnapshot)
                {
                    var snapBoxes = await _catalogDb.Database
                        .SqlQueryRaw<BoxStagingResult>($"SELECT BoxNumber, 0 AS BoxWeight, BoxLocation FROM {plSnapshotBoxes} WHERE BoxNumber IN (" +
                            string.Join(",", allBoxNumbers) + ")")
                        .ToListAsync();
                    foreach (var s in snapBoxes)
                        boxStaging[s.BoxNumber] = (0, s.BoxLocation);
                }
                else
                {
                    var staging = await _catalogDb.Database
                        .SqlQueryRaw<BoxStagingResult>("SELECT CAST(BoxNumber AS INT) AS BoxNumber, Weight AS BoxWeight, BoxLocation FROM dbo.boxstatingfromkphg WHERE BoxNumber IN (" +
                            string.Join(",", allBoxNumbers) + ")")
                        .ToListAsync();
                    foreach (var s in staging)
                        boxStaging[s.BoxNumber] = (s.BoxWeight, s.BoxLocation);
                }
            }
            catch { /* table may not exist */ }
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

        // Replace individual showlot boxes with packed boxes if packing order exists
        var packingOrder = await _db.PackingOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.ShipmentId == id);

        if (packingOrder != null)
        {
            var packedBoxes = await _db.PackedBoxes
                .Include(b => b.ShowLots)
                .Where(b => b.PackingOrderId == packingOrder.Id && b.Status == "Approved")
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
                    var pbDim = !string.IsNullOrEmpty(pb.BoxType) && dimLookup.TryGetValue(pb.BoxType, out var pbd) ? pbd : null;
                    var tare = pbDim?.WeightKg ?? 0m;
                    var pbSkins = pb.ShowLots.Sum(s => s.Skins);
                    remainingLines.Add(new
                    {
                        LotNumber = 0,
                        BoxNumber = pb.Id,
                        BoxType = pb.BoxType + " (packed)",
                        Skins = pbSkins,
                        GrossWeightKg = pb.Weight,
                        NetWeightKg = pb.Weight - tare,
                        VolumeM3 = vol,
                        TareWeightKg = tare,
                        Location = "",
                        PackedShowLots = pb.ShowLots.Select(sl => new { sl.BoxNumber, sl.LotNumber, sl.Skins }).ToList()
                    });
                    newGross += pb.Weight;
                    newNet += pb.Weight - tare;
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
            BuyerName = po.Shipment?.Buyer?.Name ?? "",
            BuyerNumber = po.Shipment?.Buyer?.BuyerNumber ?? "",
            ShipperName = po.Shipment?.Shipper?.Name ?? "",
            po.Status,
            po.Type,
            po.CreatedAt,
            BoxCount = po.Lines.Count,
            Lines = po.Lines.Select(l => new
            {
                l.Id,
                l.BoxNumber,
                l.LotNumber,
                l.Skins,
                l.BoxType,
                l.Location,
                l.PackedBoxId
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
                var boxes = root.Element("ShowLotBoxes")?.Elements("Box").Select(b => new
                {
                    BoxNumber = int.TryParse(b.Element("BoxNumber")?.Value, out var bn) ? bn : 0,
                    LotNumber = int.TryParse(b.Element("LotNumber")?.Value, out var ln) ? ln : 0,
                    Skins = int.TryParse(b.Element("Skins")?.Value, out var sk) ? sk : 0,
                    BoxType = b.Element("BoxType")?.Value ?? ""
                }).ToList() ?? new();

                results.Add(new
                {
                    FileName = fileName,
                    Folder = folder,
                    PackingOrderNumber = root.Element("PackingOrderNumber")?.Value ?? "",
                    ShipmentNumber = root.Element("ShipmentNumber")?.Value ?? "",
                    Status = root.Element("Status")?.Value ?? "",
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

    [Function("PackShowLots")]
    public async Task<HttpResponseData> PackShowLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shipments/packing-orders/{packingOrderId:int}/pack")] HttpRequestData req,
        int packingOrderId)
    {
        var body = await req.ReadFromJsonAsync<PackShowLotsDto>();
        if (body == null || string.IsNullOrEmpty(body.BoxType) || body.ShowLotLineIds == null || body.ShowLotLineIds.Count == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var order = await _db.PackingOrders
            .Include(o => o.Lines)
            .Include(o => o.Shipment).ThenInclude(s => s!.Buyer)
            .Include(o => o.Shipment).ThenInclude(s => s!.Shipper)
            .FirstOrDefaultAsync(o => o.Id == packingOrderId);
        if (order == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // Get box type dimensions
        var dimensions = await _db.Set<BoxTypeDimension>()
            .FirstOrDefaultAsync(d => d.BoxType == body.BoxType);

        var packedBox = new PackedBox
        {
            PackingOrderId = packingOrderId,
            BoxType = body.BoxType,
            Weight = body.Weight,
            HeightM = dimensions?.HeightM ?? 0,
            WidthM = dimensions?.WidthM ?? 0,
            LengthM = dimensions?.LengthM ?? 0,
            Status = "Packed"
        };
        _db.PackedBoxes.Add(packedBox);
        await _db.SaveChangesAsync();

        // Assign selected showlot lines to this packed box
        var lines = order.Lines.Where(l => body.ShowLotLineIds.Contains(l.Id)).ToList();
        foreach (var line in lines)
        {
            line.PackedBoxId = packedBox.Id;
        }
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            success = true,
            packedBoxId = packedBox.Id,
            boxType = packedBox.BoxType,
            weight = packedBox.Weight,
            showLotCount = lines.Count,
            totalSkins = lines.Sum(l => l.Skins)
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

        // Generate response XML and upload to blob
        if (_blobStorage != null)
        {
            try
            {
                var xml = GeneratePackedBoxResponseXml(packedBox);
                var blobName = $"completed/{packedBox.PackingOrder!.PackingOrderNumber}-BOX{packedBox.Id}.xml";
                await _blobStorage.UploadPackingXmlAsync(blobName, xml);
                _logger.LogInformation("Packed box response XML {Name} uploaded", blobName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload packed box response XML for box {Id}", packedBoxId);
            }
        }

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

    private static string GeneratePackedBoxResponseXml(PackedBox packedBox)
    {
        var order = packedBox.PackingOrder!;
        var shipment = order.Shipment!;
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("PackedBoxResponse",
                new XElement("PackingOrderNumber", order.PackingOrderNumber),
                new XElement("ShipmentNumber", shipment.ShipmentNumber),
                new XElement("PackedBoxId", packedBox.Id),
                new XElement("BoxType", packedBox.BoxType),
                new XElement("Weight", packedBox.Weight),
                new XElement("Dimensions",
                    new XElement("HeightM", packedBox.HeightM),
                    new XElement("WidthM", packedBox.WidthM),
                    new XElement("LengthM", packedBox.LengthM)
                ),
                new XElement("Buyer",
                    new XElement("Name", shipment.Buyer?.Name ?? ""),
                    new XElement("BuyerNumber", shipment.Buyer?.BuyerNumber ?? "")
                ),
                new XElement("Shipper",
                    new XElement("Name", shipment.Shipper?.Name ?? "")
                ),
                new XElement("ShowLots",
                    new XAttribute("Count", packedBox.ShowLots.Count),
                    new XAttribute("TotalSkins", packedBox.ShowLots.Sum(l => l.Skins)),
                    packedBox.ShowLots.Select(l =>
                        new XElement("ShowLot",
                            new XElement("BoxNumber", l.BoxNumber),
                            new XElement("LotNumber", l.LotNumber),
                            new XElement("Skins", l.Skins),
                            new XElement("OriginalBoxType", l.BoxType)
                        )
                    )
                ),
                new XElement("ApprovedAt", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
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

    private class BoxStagingResult
    {
        public int BoxNumber { get; set; }
        public decimal? BoxWeight { get; set; }
        public string? BoxLocation { get; set; }
    }

    private class CatalogLotResult
    {
        public int LotNumber { get; set; }
        public string IncludedBoxNumbers { get; set; } = "";
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
}

public class UpdateShipmentStatusDto
{
    public string Status { get; set; } = "";
    public string? TrackingNumber { get; set; }
}

public class UpdateShipmentDto
{
    public int? ShippingAddressId { get; set; }
    public string? TrackingNumber { get; set; }
    public string? Notes { get; set; }
}

public class UpdatePackingOrderStatusDto
{
    public string Status { get; set; } = "";
}

public class PackShowLotsDto
{
    public string BoxType { get; set; } = "";
    public decimal Weight { get; set; }
    public List<int> ShowLotLineIds { get; set; } = new();
}
