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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ShipmentFunctions(AuctionDbContext db)
    {
        _db = db;
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
                InvoiceCount = s.Lines.Count,
                Invoices = s.Lines.Select(l => new
                {
                    l.InvoiceId,
                    InvoiceNumber = l.Invoice != null ? l.Invoice.InvoiceNumber : "",
                    l.BoxNumber,
                    l.Notes
                }).ToList()
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(shipments, JsonOptions));
        return response;
    }

    [Function("GetReleasedInvoicesForShipment")]
    public async Task<HttpResponseData> GetReleasedInvoices(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipments/released-invoices")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var buyerFilter = query["buyerId"];

        var q = _db.Invoices
            .Include(i => i.Buyer)
            .Include(i => i.Broker)
            .Where(i => i.ShippingStatus == "Released" && !i.IsCreditNote)
            .AsQueryable();

        if (int.TryParse(buyerFilter, out var bId))
            q = q.Where(i => i.BuyerId == bId);

        // Exclude invoices already in a shipment
        var shippedInvoiceIds = await _db.ShipmentLines.Select(l => l.InvoiceId).Distinct().ToListAsync();

        var invoices = await q
            .Where(i => !shippedInvoiceIds.Contains(i.Id))
            .OrderBy(i => i.BuyerId).ThenBy(i => i.InvoiceNumber)
            .Select(i => new
            {
                i.Id,
                i.InvoiceNumber,
                i.BuyerId,
                BuyerName = i.Buyer != null ? i.Buyer.Name : "",
                BuyerNumber = i.Buyer != null ? i.Buyer.BuyerNumber : "",
                BrokerName = i.Broker != null ? i.Broker.CompanyName : "",
                i.TotalAmount,
                i.Currency,
                i.Status,
                i.ShippingStatus
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(invoices, JsonOptions));
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

        if (body.InvoiceIds == null || !body.InvoiceIds.Any())
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            await bad.WriteStringAsync(JsonSerializer.Serialize(new { error = "At least one invoice is required" }, JsonOptions));
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

        foreach (var invoiceId in body.InvoiceIds)
        {
            shipment.Lines.Add(new ShipmentLine
            {
                InvoiceId = invoiceId
            });
        }

        _db.Shipments.Add(shipment);

        // Update invoice shipping status
        var invoices = await _db.Invoices.Where(i => body.InvoiceIds.Contains(i.Id)).ToListAsync();
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
        var invoiceIds = shipment.Lines.Select(l => l.InvoiceId).ToList();
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
        var invoiceIds = shipment.Lines.Select(l => l.InvoiceId).ToList();
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
}

public class CreateShipmentDto
{
    public int ShipperId { get; set; }
    public int BuyerId { get; set; }
    public int? ShippingAddressId { get; set; }
    public string? TrackingNumber { get; set; }
    public string? Notes { get; set; }
    public List<int> InvoiceIds { get; set; } = new();
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
