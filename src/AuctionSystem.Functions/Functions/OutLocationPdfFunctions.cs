using AuctionSystem.Domain.Data;
using AuctionSystem.Functions.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Net;

namespace AuctionSystem.Functions.Functions;

// Print-out of an OUT location's contents: the shipment info plus every box standing there
// (moved storage boxes + packed showlot cartons), each with a scannable Code 128 barcode.
public class OutLocationPdfFunctions
{
    private readonly AuctionDbContext _db;
    private readonly ILogger<OutLocationPdfFunctions> _logger;

    public OutLocationPdfFunctions(AuctionDbContext db, ILogger<OutLocationPdfFunctions> logger)
    {
        _db = db;
        _logger = logger;
    }

    [RequireRole("Admin")]
    [Function("GetOutLocationPdf")]
    public async Task<HttpResponseData> GetOutLocationPdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipments/{id:int}/location-pdf")] HttpRequestData req,
        int id)
    {
        var shipment = await _db.Shipments
            .Include(s => s.Buyer)
            .Include(s => s.Shipper)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (shipment == null)
            return req.CreateResponse(HttpStatusCode.NotFound);

        var orders = await _db.PackingOrders.Include(p => p.Lines)
            .Where(p => p.ShipmentId == id).ToListAsync();

        var storageBoxes = orders.Where(o => o.Type == "Packing")
            .SelectMany(o => o.Lines)
            .Where(l => l.MovedToOutAt != null)
            .OrderBy(l => l.BoxNumber)
            .ToList();

        var showLotOrderIds = orders.Where(o => o.Type == "ShowLot").Select(o => o.Id).ToList();
        var cartons = await _db.PackedBoxes.Include(b => b.ShowLots)
            .Where(b => showLotOrderIds.Contains(b.PackingOrderId))
            .OrderBy(b => b.Id)
            .ToListAsync();

        QuestPDF.Settings.License = LicenseType.Community;
        var pdfBytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().PaddingBottom(8).Column(col =>
                {
                    col.Item().Text($"{shipment.OutLocation ?? "Lane"} — Shipment {shipment.ShipmentNumber}").Bold().FontSize(14);
                    col.Item().Text($"Buyer: {shipment.Buyer?.BuyerNumber} - {shipment.Buyer?.Name}    Shipper: {shipment.Shipper?.Name}").FontSize(9);
                    col.Item().Text($"Date: {DateTime.UtcNow:yyyy-MM-dd}    Boxes at location: {storageBoxes.Count + cartons.Count}" +
                        (shipment.Pallets is > 0 ? $"    Pallets: {shipment.Pallets}" : "") +
                        (string.IsNullOrEmpty(shipment.TrackingNumber) ? "" : $"    AWB: {shipment.TrackingNumber}")).FontSize(9);
                    col.Item().PaddingTop(4).LineHorizontal(0.75f);
                });

                page.Content().Column(col =>
                {
                    if (storageBoxes.Count > 0)
                    {
                        col.Item().PaddingTop(4).Text($"Storage boxes ({storageBoxes.Count})").Bold().FontSize(10);
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(60);   // Box #
                                c.ConstantColumn(60);   // Lot #
                                c.ConstantColumn(50);   // Type
                                c.ConstantColumn(45);   // Skins
                                c.ConstantColumn(130);  // Barcode
                                c.ConstantColumn(25);   // Checkbox
                                c.RelativeColumn();     // Remarks (handwriting space)
                            });
                            table.Header(h =>
                            {
                                foreach (var t in new[] { "Box #", "Lot #", "Type", "Skins", "Barcode", "", "Remarks" })
                                    h.Cell().BorderBottom(1).PaddingBottom(2).Text(t).Bold().FontSize(8);
                            });
                            foreach (var b in storageBoxes)
                            {
                                table.Cell().PaddingVertical(5).Text(b.BoxNumber.ToString()).Bold();
                                table.Cell().PaddingVertical(5).Text(b.LotNumber.ToString());
                                table.Cell().PaddingVertical(5).Text(b.BoxType);
                                table.Cell().PaddingVertical(5).Text(b.Skins.ToString());
                                table.Cell().PaddingVertical(3).MaxWidth(130).Height(26).Element(e => RenderBarcode(e, b.BoxNumber.ToString()));
                                table.Cell().PaddingVertical(6).AlignCenter().Width(12).Height(12).Border(1);
                                table.Cell().PaddingVertical(5).PaddingHorizontal(4).BorderBottom(0.5f).BorderColor(Colors.Grey.Medium).Text("");
                            }
                        });
                    }

                    if (cartons.Count > 0)
                    {
                        col.Item().PaddingTop(10).Text($"Packed cartons ({cartons.Count})").Bold().FontSize(10);
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(80);   // Carton #
                                c.ConstantColumn(45);   // Type
                                c.ConstantColumn(60);   // Weight
                                c.ConstantColumn(45);   // Skins
                                c.ConstantColumn(130);  // Barcode
                                c.ConstantColumn(25);   // Checkbox
                                c.RelativeColumn();     // Remarks (handwriting space)
                            });
                            table.Header(h =>
                            {
                                foreach (var t in new[] { "Carton #", "Type", "Weight (kg)", "Skins", "Barcode", "", "Remarks" })
                                    h.Cell().BorderBottom(1).PaddingBottom(2).Text(t).Bold().FontSize(8);
                            });
                            foreach (var c in cartons)
                            {
                                table.Cell().PaddingVertical(5).Text(c.BoxNumber).Bold();
                                table.Cell().PaddingVertical(5).Text(c.BoxType);
                                table.Cell().PaddingVertical(5).Text(c.GrossWeight > 0 ? c.GrossWeight.ToString("N2") : c.Weight.ToString("N2"));
                                table.Cell().PaddingVertical(5).Text(c.ShowLots.Sum(l => l.Skins).ToString());
                                table.Cell().PaddingVertical(3).MaxWidth(130).Height(26).Element(e => RenderBarcode(e, c.BoxNumber));
                                table.Cell().PaddingVertical(6).AlignCenter().Width(12).Height(12).Border(1);
                                table.Cell().PaddingVertical(5).PaddingHorizontal(4).BorderBottom(0.5f).BorderColor(Colors.Grey.Medium).Text("");
                            }
                        });
                    }

                    if (storageBoxes.Count == 0 && cartons.Count == 0)
                        col.Item().PaddingTop(10).Text("No boxes at this location yet.").Italic();
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();

        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/pdf");
        resp.Headers.Add("Content-Disposition", $"inline; filename=\"{shipment.OutLocation ?? "OUT"} - {shipment.ShipmentNumber}.pdf\"");
        await resp.Body.WriteAsync(pdfBytes);
        return resp;
    }

    // Print-out of a packing order: the same box overview the Boxes page shows (box, lot, skins,
    // type, location) with a scannable barcode per box.
    [RequireRole("Admin")]
    [Function("GetPackingOrderPdf")]
    public async Task<HttpResponseData> GetPackingOrderPdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipments/packing-orders/{id:int}/pdf")] HttpRequestData req,
        int id)
    {
        var order = await _db.PackingOrders
            .Include(p => p.Lines)
            .Include(p => p.Shipment).ThenInclude(s => s!.Buyer)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (order == null)
            return req.CreateResponse(HttpStatusCode.NotFound);

        var lines = order.Lines.OrderBy(l => l.BoxNumber).ToList();
        var lane = order.Shipment?.OutLocation;

        QuestPDF.Settings.License = LicenseType.Community;
        var pdfBytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().PaddingBottom(8).Column(col =>
                {
                    col.Item().Text($"Packing order {order.PackingOrderNumber} — Shipment {order.Shipment?.ShipmentNumber}").Bold().FontSize(14);
                    col.Item().Text($"Buyer: {order.Shipment?.Buyer?.BuyerNumber} - {order.Shipment?.Buyer?.Name}    Lane: {lane ?? "—"}    Status: {order.Status}").FontSize(9);
                    col.Item().Text($"Date: {DateTime.UtcNow:yyyy-MM-dd}    Boxes: {lines.Count}    Skins: {lines.Sum(l => l.Skins):N0}").FontSize(9);
                    col.Item().PaddingTop(4).LineHorizontal(0.75f);
                });

                page.Content().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(60);   // Box #
                        c.ConstantColumn(60);   // Lot #
                        c.ConstantColumn(45);   // Skins
                        c.ConstantColumn(55);   // Type
                        c.ConstantColumn(80);   // Location
                        c.RelativeColumn();     // Barcode
                    });
                    table.Header(h =>
                    {
                        foreach (var t in new[] { "Box #", "Lot #", "Skins", "Type", "Location", "Barcode" })
                            h.Cell().BorderBottom(1).PaddingBottom(2).Text(t).Bold().FontSize(8);
                    });
                    foreach (var l in lines)
                    {
                        // Moved boxes are at the lane, the rest at their storage location.
                        var location = l.MovedToOutAt != null ? (lane ?? "LANE") : (string.IsNullOrEmpty(l.Location) ? "—" : l.Location);
                        table.Cell().PaddingVertical(5).Text(l.BoxNumber.ToString()).Bold();
                        table.Cell().PaddingVertical(5).Text(l.LotNumber.ToString());
                        table.Cell().PaddingVertical(5).Text(l.Skins.ToString());
                        table.Cell().PaddingVertical(5).Text(l.BoxType);
                        table.Cell().PaddingVertical(5).Text(location);
                        table.Cell().PaddingVertical(3).MaxWidth(130).Height(26).Element(e => RenderBarcode(e, l.BoxNumber.ToString()));
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();

        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/pdf");
        resp.Headers.Add("Content-Disposition", $"inline; filename=\"{order.PackingOrderNumber}.pdf\"");
        await resp.Body.WriteAsync(pdfBytes);
        return resp;
    }

    private static void RenderBarcode(IContainer container, string data)
    {
        var segments = Code128.EncodeB(data);
        container.Row(row =>
        {
            foreach (var (isBar, width) in segments)
                row.RelativeItem(width).Background(isBar ? Colors.Black : Colors.White);
        });
    }
}
