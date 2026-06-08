using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

public class ShipmentFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ShipmentFunctions(AuctionDbContext db, CatalogDbContext catalogDb)
    {
        _db = db;
        _catalogDb = catalogDb;
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

        // Update invoice shipping status for affected invoices
        var invoiceIds = lotInvoiceMap.Values.Distinct().ToList();
        var invoices = await _db.Invoices.Where(i => invoiceIds.Contains(i.Id)).ToListAsync();
        foreach (var inv in invoices)
        {
            inv.ShippingStatus = "InShipment";
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { success = true, id = shipment.Id, shipmentNumber = shipment.ShipmentNumber }, JsonOptions));
        return response;
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

        var validStatuses = new[] { "Pending", "Shipped", "Delivered", "Cancelled" };
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

        if (shipment.Status != "Pending")
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            await bad.WriteStringAsync(JsonSerializer.Serialize(new { error = "Can only delete pending shipments" }, JsonOptions));
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

        // Get CatalogLots to resolve box numbers
        var catalogLots = await _catalogDb.CatalogLots
            .Where(cl => lotNumbers.Contains(cl.LotNumber))
            .Select(cl => new { cl.LotNumber, cl.IncludedBoxNumbers })
            .ToListAsync();

        var allBoxNumbers = catalogLots
            .Where(cl => !string.IsNullOrEmpty(cl.IncludedBoxNumbers))
            .SelectMany(cl => cl.IncludedBoxNumbers!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0).Where(n => n > 0))
            .Distinct().ToList();

        // Get box info (skins, box type)
        var boxInfo = new Dictionary<int, (int Skins, string BoxType)>();
        if (allBoxNumbers.Count > 0)
        {
            var boxData = await _catalogDb.Database
                .SqlQueryRaw<BoxViewResult>("SELECT BoxNumber, Skins, BoxType FROM auction.boxes WHERE BoxNumber IN (" +
                    string.Join(",", allBoxNumbers) + ")")
                .ToListAsync();
            foreach (var b in boxData)
                boxInfo[b.BoxNumber] = (b.Skins, b.BoxType);
        }

        // Get box staging (actual weight)
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
        }

        // Get box type dimensions
        var dimensions = await _db.BoxTypeDimensions.ToListAsync();
        var dimLookup = dimensions.ToDictionary(d => d.BoxType, d => d);

        // Build packing list
        var packingLines = new List<object>();
        decimal totalGrossWeight = 0, totalNetWeight = 0, totalVolume = 0;
        int totalBoxes = 0, totalSkins = 0;

        foreach (var cl in catalogLots)
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

    private class BoxViewResult
    {
        public int BoxNumber { get; set; }
        public int Skins { get; set; }
        public string BoxType { get; set; } = "";
    }

    private class BoxStagingResult
    {
        public int BoxNumber { get; set; }
        public decimal? BoxWeight { get; set; }
        public string? BoxLocation { get; set; }
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
