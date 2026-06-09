using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AuctionSystem.Functions.Services;

public class PackingListLine
{
    public string Text { get; set; } = "";
    public string LotNo { get; set; } = "";
    public string Carton { get; set; } = "";
    public int Skins { get; set; }
    public decimal HammerPrice { get; set; }
    public decimal VolumeM3 { get; set; }
    public decimal NetWeight { get; set; }
    public decimal GrossWeight { get; set; }
    public bool IsShowLot { get; set; }
    public bool IsPackedBoxSummary { get; set; }
}

public class PackingListData
{
    public string ShipmentNumber { get; set; } = "";
    public string ForwardingAgent { get; set; } = "";
    public string InvoiceAccount { get; set; } = "";
    public string AwbNumber { get; set; } = "";
    public string Date { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Marking { get; set; } = "";

    // Buyer / bill-to address
    public string BuyerName { get; set; } = "";
    public List<string> BuyerAddressLines { get; set; } = new();

    // Ship-to address
    public string ShipToName { get; set; } = "";
    public List<string> ShipToAddressLines { get; set; } = new();

    public List<PackingListLine> Lines { get; set; } = new();

    // Grand totals
    public int TotalCartons { get; set; }
    public int TotalSkins { get; set; }
    public decimal TotalPrice { get; set; }
    public decimal TotalVolume { get; set; }
    public decimal TotalNetWeight { get; set; }
    public decimal TotalGrossWeight { get; set; }

    public bool IsShippingInvoice { get; set; }
}

public static class PackingListPdfService
{
    private static readonly string LogoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "kopenhagenfur-logo.png");

    public static byte[] GeneratePdf(PackingListData data)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginTop(25);
                page.MarginBottom(25);
                page.MarginLeft(35);
                page.MarginRight(35);
                page.DefaultTextStyle(x => x.FontSize(8));

                page.Header().Element(c => ComposeHeader(c, data));
                page.Content().Element(c => ComposeContent(c, data));
                page.Footer().Element(c => ComposeFooter(c, data));
            });
        });

        using var stream = new MemoryStream();
        document.GeneratePdf(stream);
        return stream.ToArray();
    }

    private static void ComposeHeader(IContainer container, PackingListData data)
    {
        container.Column(col =>
        {
            // Kopenhagen Fur letterhead
            col.Item().Row(row =>
            {
                row.RelativeItem(7).Column(left =>
                {
                    left.Item().Text("KOPENHAGEN FUR").Bold().FontSize(9);
                    left.Item().Height(6);
                    left.Item().Row(infoRow =>
                    {
                        infoRow.RelativeItem().Text("UL. SKŁADOWA 10").FontSize(6.5f);
                        infoRow.RelativeItem().Text("KOPENHAGENFUR.COM").FontSize(6.5f);
                    });
                    left.Item().Height(2);
                    left.Item().Row(infoRow =>
                    {
                        infoRow.RelativeItem().Text("62-023 ŻERNIKI").FontSize(6.5f);
                        infoRow.RelativeItem().Text("NIP: 5253073718").FontSize(6.5f);
                    });
                    left.Item().Height(2);
                    left.Item().Row(infoRow =>
                    {
                        infoRow.RelativeItem().Text("POLAND").FontSize(6.5f);
                        infoRow.RelativeItem().Text("cargobooking@kopenhagenfur.com").FontSize(6.5f);
                    });
                });
                row.RelativeItem(3).AlignRight().AlignTop()
                    .Height(45)
                    .Image(LogoPath, ImageScaling.FitArea);
            });

            col.Item().Height(5);
            col.Item().LineHorizontal(0.5f);
            col.Item().Height(15);

            // Buyer address + Packing list info
            col.Item().Row(row =>
            {
                row.RelativeItem(5).Column(left =>
                {
                    left.Item().Text(data.BuyerName).FontSize(8);
                    foreach (var line in data.BuyerAddressLines)
                        left.Item().Text(line).FontSize(8);
                    left.Item().Height(10);
                    if (data.ShipToAddressLines.Count > 0)
                    {
                        left.Item().Text("Ship to:").FontSize(8);
                        left.Item().Text(data.ShipToName).FontSize(8);
                        foreach (var line in data.ShipToAddressLines)
                            left.Item().Text(line).FontSize(8);
                    }
                });

                row.RelativeItem(5).Column(right =>
                {
                    right.Item().Text(data.IsShippingInvoice ? "Shipping invoice" : "Packing list").Bold().FontSize(16);
                    right.Item().Height(10);
                    AddHeaderField(right, "Forwarding Agent", data.ForwardingAgent);
                    AddHeaderField(right, "Invoice account", data.InvoiceAccount);
                    AddHeaderField(right, "KF Ref.", data.ShipmentNumber);
                    AddHeaderField(right, "AWB number", data.AwbNumber);
                    AddHeaderField(right, "Date", data.Date);
                    AddHeaderField(right, "Destination", data.Destination);
                    AddHeaderField(right, "Marking", data.Marking);
                });
            });

            col.Item().Height(15);
        });
    }

    private static void AddHeaderField(ColumnDescriptor col, string label, string value)
    {
        col.Item().Row(r =>
        {
            r.ConstantItem(120).Text(label).FontSize(8);
            r.RelativeItem().Text(value).Bold().FontSize(8);
        });
    }

    private static void ComposeContent(IContainer container, PackingListData data)
    {
        var inv = data.IsShippingInvoice;
        var showLotLines = data.Lines.Where(l => l.IsShowLot || l.IsPackedBoxSummary).ToList();
        var storageLines = data.Lines.Where(l => !l.IsShowLot && !l.IsPackedBoxSummary).ToList();

        container.Column(col =>
        {
            // ShowLot section first (if any)
            if (showLotLines.Count > 0)
            {
                col.Item().Element(c => ComposeTable(c, showLotLines, inv));

                // Showlot subtotal
                var slBoxes = showLotLines.Count(l => l.IsPackedBoxSummary);
                var slSkins = showLotLines.Where(l => l.IsPackedBoxSummary).Sum(l => l.Skins);
                var slPrice = showLotLines.Where(l => l.IsShowLot).Sum(l => l.HammerPrice * l.Skins);
                var slVol = showLotLines.Where(l => l.IsPackedBoxSummary).Sum(l => l.VolumeM3);
                var slNet = showLotLines.Where(l => l.IsPackedBoxSummary).Sum(l => l.NetWeight);
                var slGross = showLotLines.Where(l => l.IsPackedBoxSummary).Sum(l => l.GrossWeight);
                col.Item().Element(c => ComposeSectionTotal(c, "Showlot total", slBoxes, slSkins, slPrice, slVol, slNet, slGross, inv));

                // Page break before storage boxes (if any)
                if (storageLines.Count > 0)
                    col.Item().PageBreak();
            }

            // Storage boxes section
            if (storageLines.Count > 0)
            {
                col.Item().Element(c => ComposeTable(c, storageLines, inv));

                // Storage subtotal
                var stBoxes = storageLines.Count;
                var stSkins = storageLines.Sum(l => l.Skins);
                var stPrice = storageLines.Sum(l => l.HammerPrice * l.Skins);
                var stVol = storageLines.Sum(l => l.VolumeM3);
                var stNet = storageLines.Sum(l => l.NetWeight);
                var stGross = storageLines.Sum(l => l.GrossWeight);
                col.Item().Element(c => ComposeSectionTotal(c, "Storage total", stBoxes, stSkins, stPrice, stVol, stNet, stGross, inv));
            }

            // Grand total at the end
            col.Item().Element(c => ComposeGrandTotal(c, data));
        });
    }

    private static void ComposeTable(IContainer container, List<PackingListLine> lines, bool isInvoice)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.RelativeColumn(5);   // Text (description)
                c.RelativeColumn(1.5f); // Lot no.
                c.RelativeColumn(1.5f); // Cartons (box number)
                c.RelativeColumn(1.2f); // No. of skins
                if (isInvoice) c.RelativeColumn(1.5f); // Price
                c.RelativeColumn(1);   // Volume
                c.RelativeColumn(1.2f); // Net Weight
                c.RelativeColumn(1.2f); // Gross Weight
            });

            table.Header(header =>
            {
                header.Cell().BorderBottom(1).PaddingBottom(3).Text("Text").Bold().FontSize(7.5f);
                header.Cell().BorderBottom(1).PaddingBottom(3).AlignRight().Text("Lot no.").Bold().FontSize(7.5f);
                header.Cell().BorderBottom(1).PaddingBottom(3).AlignRight().Text("Cartons").Bold().FontSize(7.5f);
                header.Cell().BorderBottom(1).PaddingBottom(3).AlignRight().Text("No. of skins").Bold().FontSize(7.5f);
                if (isInvoice) header.Cell().BorderBottom(1).PaddingBottom(3).AlignRight().Text("Price").Bold().FontSize(7.5f);
                header.Cell().BorderBottom(1).PaddingBottom(3).AlignRight().Text("Volume").Bold().FontSize(7.5f);
                header.Cell().BorderBottom(1).PaddingBottom(3).AlignRight().Text("Net Weight").Bold().FontSize(7.5f);
                header.Cell().BorderBottom(1).PaddingBottom(3).AlignRight().Text("Gross weight").Bold().FontSize(7.5f);
            });

            foreach (var line in lines)
            {
                if (line.IsShowLot)
                {
                    table.Cell().PaddingVertical(2).PaddingLeft(2).Text(line.Text).FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.LotNo).FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.Carton).FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.Skins.ToString("N0")).FontSize(7.5f);
                    if (isInvoice) table.Cell().PaddingVertical(2).AlignRight().Text(line.HammerPrice > 0 ? (line.HammerPrice * line.Skins).ToString("N2") : "").FontSize(7.5f);
                    table.Cell().PaddingVertical(2).Text("").FontSize(7.5f);
                    table.Cell().PaddingVertical(2).Text("").FontSize(7.5f);
                    table.Cell().PaddingVertical(2).Text("").FontSize(7.5f);
                }
                else if (line.IsPackedBoxSummary)
                {
                    table.Cell().PaddingVertical(2).Text("").FontSize(7.5f);
                    table.Cell().PaddingVertical(2).Text("").FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.Carton).Bold().FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.Skins.ToString("N0")).Bold().FontSize(7.5f);
                    if (isInvoice) table.Cell().PaddingVertical(2).Text("").FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.VolumeM3 > 0 ? line.VolumeM3.ToString("N4") : "").Bold().FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.NetWeight > 0 ? line.NetWeight.ToString("N2") : "").Bold().FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.GrossWeight > 0 ? line.GrossWeight.ToString("N2") : "").Bold().FontSize(7.5f);
                }
                else
                {
                    var price = line.HammerPrice * line.Skins;
                    table.Cell().PaddingVertical(2).PaddingLeft(2).Text(line.Text).FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.LotNo).FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.Carton).FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.Skins.ToString("N0")).FontSize(7.5f);
                    if (isInvoice) table.Cell().PaddingVertical(2).AlignRight().Text(price > 0 ? price.ToString("N2") : "").FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.VolumeM3 > 0 ? line.VolumeM3.ToString("N4") : "").FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.NetWeight > 0 ? line.NetWeight.ToString("N2") : "").FontSize(7.5f);
                    table.Cell().PaddingVertical(2).AlignRight().Text(line.GrossWeight > 0 ? line.GrossWeight.ToString("N2") : "").FontSize(7.5f);
                }
            }
        });
    }

    private static void ComposeSectionTotal(IContainer container, string label, int boxes, int skins, decimal price, decimal vol, decimal net, decimal gross, bool isInvoice)
    {
        container.Column(col =>
        {
            col.Item().Height(5);
            col.Item().LineHorizontal(0.5f);
            col.Item().Height(3);
            col.Item().Row(row =>
            {
                row.RelativeItem(5).Text(label).Bold().FontSize(8);       // Text
                row.RelativeItem(1.5f).Text("").FontSize(8);               // Lot no. (empty)
                row.RelativeItem(1.5f).AlignRight().Text(boxes.ToString("N0")).Bold().FontSize(8);  // Cartons
                row.RelativeItem(1.2f).AlignRight().Text(skins.ToString("N0")).Bold().FontSize(8);  // Skins
                if (isInvoice) row.RelativeItem(1.5f).AlignRight().Text(price.ToString("N2")).Bold().FontSize(8); // Price
                row.RelativeItem(1).AlignRight().Text(vol.ToString("N4")).Bold().FontSize(8);       // Volume
                row.RelativeItem(1.2f).AlignRight().Text(net.ToString("N2")).Bold().FontSize(8);    // Net
                row.RelativeItem(1.2f).AlignRight().Text(gross.ToString("N2")).Bold().FontSize(8);  // Gross
            });
        });
    }

    private static void ComposeGrandTotal(IContainer container, PackingListData data)
    {
        container.Column(col =>
        {
            col.Item().Height(10);
            col.Item().LineHorizontal(1);
            col.Item().Height(5);
            col.Item().Row(row =>
            {
                row.RelativeItem(5).Text("Grand total").Bold().FontSize(9);       // Text
                row.RelativeItem(1.5f).Text("").FontSize(9);                        // Lot no. (empty)
                row.RelativeItem(1.5f).AlignRight().Text(data.TotalCartons.ToString("N0")).Bold().FontSize(9);  // Cartons
                row.RelativeItem(1.2f).AlignRight().Text(data.TotalSkins.ToString("N0")).Bold().FontSize(9);    // Skins
                if (data.IsShippingInvoice) row.RelativeItem(1.5f).AlignRight().Text(data.TotalPrice.ToString("N2")).Bold().FontSize(9); // Price
                row.RelativeItem(1).AlignRight().Text(data.TotalVolume.ToString("N4")).Bold().FontSize(9);      // Volume
                row.RelativeItem(1.2f).AlignRight().Text(data.TotalNetWeight.ToString("N2")).Bold().FontSize(9); // Net
                row.RelativeItem(1.2f).AlignRight().Text(data.TotalGrossWeight.ToString("N2")).Bold().FontSize(9); // Gross
            });
        });
    }

    private static void ComposeFooter(IContainer container, PackingListData data)
    {
        container.Column(col =>
        {
            // Page number only
            col.Item().AlignCenter().Text(text =>
            {
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });
    }
}
