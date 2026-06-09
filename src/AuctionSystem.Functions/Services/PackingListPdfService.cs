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
    public decimal VolumeM3 { get; set; }
    public decimal NetWeight { get; set; }
    public decimal GrossWeight { get; set; }
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
    public decimal TotalVolume { get; set; }
    public decimal TotalNetWeight { get; set; }
    public decimal TotalGrossWeight { get; set; }
}

public static class PackingListPdfService
{
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
                    left.Item().Text("KOPENHAGEN FUR a.m.b.a.").Bold().FontSize(9);
                    left.Item().Row(infoRow =>
                    {
                        infoRow.RelativeItem().Text("LANGAGERVEJ 60").FontSize(6.5f);
                        infoRow.RelativeItem().Text("TEL +45 4326 1000").FontSize(6.5f);
                        infoRow.RelativeItem().Text("KOPENHAGENFUR.COM").FontSize(6.5f);
                    });
                    left.Item().Row(infoRow =>
                    {
                        infoRow.RelativeItem().Text("DK-2600 GLOSTRUP").FontSize(6.5f);
                        infoRow.RelativeItem().Text("FAX +45 4326 1126").FontSize(6.5f);
                        infoRow.RelativeItem().Text("VAT NO. DK15275413").FontSize(6.5f);
                    });
                    left.Item().Height(3);
                    left.Item().Row(infoRow =>
                    {
                        infoRow.RelativeItem().Text("Shipping").FontSize(6.5f);
                        infoRow.RelativeItem().Text("TEL +45 4326 1000").FontSize(6.5f);
                        infoRow.RelativeItem().Text("shipping@kopenhagenfur.com").FontSize(6.5f);
                    });
                });
                row.RelativeItem(3).AlignRight().Text("KOPENHAGEN\nFUR").Bold().FontSize(18);
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
                    right.Item().Text("Packing list").Bold().FontSize(16);
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
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.RelativeColumn(5);   // Text (description)
                c.RelativeColumn(1.5f); // Lot no.
                c.RelativeColumn(1.5f); // Cartons (box number)
                c.RelativeColumn(1.2f); // No. of skins
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
                header.Cell().BorderBottom(1).PaddingBottom(3).AlignRight().Text("Volume").Bold().FontSize(7.5f);
                header.Cell().BorderBottom(1).PaddingBottom(3).AlignRight().Text("Net Weight").Bold().FontSize(7.5f);
                header.Cell().BorderBottom(1).PaddingBottom(3).AlignRight().Text("Gross weight").Bold().FontSize(7.5f);
            });

            foreach (var line in data.Lines)
            {
                table.Cell().Padding(2).Text(line.Text).FontSize(7.5f);
                table.Cell().Padding(2).AlignRight().Text(line.LotNo).FontSize(7.5f);
                table.Cell().Padding(2).AlignRight().Text(line.Carton).FontSize(7.5f);
                table.Cell().Padding(2).AlignRight().Text(line.Skins.ToString("N0")).FontSize(7.5f);
                table.Cell().Padding(2).AlignRight().Text(line.VolumeM3 > 0 ? line.VolumeM3.ToString("N4") : "").FontSize(7.5f);
                table.Cell().Padding(2).AlignRight().Text(line.NetWeight > 0 ? line.NetWeight.ToString("N2") : "").FontSize(7.5f);
                table.Cell().Padding(2).AlignRight().Text(line.GrossWeight > 0 ? line.GrossWeight.ToString("N2") : "").FontSize(7.5f);
            }
        });
    }

    private static void ComposeFooter(IContainer container, PackingListData data)
    {
        container.Column(col =>
        {
            col.Item().Height(10);
            col.Item().LineHorizontal(0.5f);
            col.Item().Height(5);

            // Grand total row
            col.Item().Row(row =>
            {
                row.RelativeItem(5).Text("Grand total").Bold().FontSize(9);
                row.RelativeItem(1.5f).AlignRight().Text(data.TotalCartons.ToString("N0")).Bold().FontSize(8);
                row.RelativeItem(1.2f).AlignRight().Text(data.TotalSkins.ToString("N0")).Bold().FontSize(8);
                row.RelativeItem(1).AlignRight().Text(data.TotalVolume.ToString("N4")).Bold().FontSize(8);
                row.RelativeItem(1.2f).AlignRight().Text(data.TotalNetWeight.ToString("N2")).Bold().FontSize(8);
                row.RelativeItem(1.2f).AlignRight().Text(data.TotalGrossWeight.ToString("N2")).Bold().FontSize(8);
            });

            col.Item().Height(10);

            // Page number
            col.Item().AlignCenter().Text(text =>
            {
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });
    }
}
