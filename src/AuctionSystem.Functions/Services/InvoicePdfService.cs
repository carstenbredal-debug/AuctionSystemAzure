using AuctionSystem.Domain.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AuctionSystem.Functions.Services;

public static class InvoicePdfService
{
    public static byte[] GeneratePdf(Invoice invoice)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginTop(30);
                page.MarginBottom(30);
                page.MarginLeft(40);
                page.MarginRight(40);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Element(c => ComposeHeader(c, invoice));
                page.Content().Element(c => ComposeContent(c, invoice));
                page.Footer().Element(c => ComposeFooter(c));
            });
        });

        using var stream = new MemoryStream();
        document.GeneratePdf(stream);
        return stream.ToArray();
    }

    private static void ComposeHeader(IContainer container, Invoice invoice)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem(6).Column(left => ComposeBuyerAddress(left, invoice));
                row.RelativeItem(4).Column(right => ComposeInvoiceInfo(right, invoice));
            });

            col.Item().Height(15);
        });
    }

    private static void ComposeBuyerAddress(ColumnDescriptor col, Invoice invoice)
    {
        col.Item().Text("Kopenhagen Fur").Bold().FontSize(14);
        col.Item().Height(10);

        col.Item().Text(invoice.Buyer?.Name ?? "").Bold().FontSize(10);

        var addressLines = new[]
        {
            invoice.Buyer?.Name2,
            invoice.Buyer?.AddressLine1,
            invoice.Buyer?.AddressLine2,
            string.Join(" ", new[] { invoice.Buyer?.PostalCode, invoice.Buyer?.City }.Where(s => !string.IsNullOrEmpty(s))),
            invoice.Buyer?.Country
        };

        foreach (var line in addressLines.Where(l => !string.IsNullOrEmpty(l)))
            col.Item().Text(line!);
    }

    private static void ComposeInvoiceInfo(ColumnDescriptor col, Invoice invoice)
    {
        var title = invoice.IsCreditNote ? "CREDIT NOTE" : "INVOICE";
        col.Item().AlignRight().Text(title).Bold().FontSize(14);
        col.Item().Height(10);

        var numberLabel = invoice.IsCreditNote ? "Credit note no. . :" : "Invoice number. . . :";
        AddInfoRow(col, numberLabel, invoice.InvoiceNumber);
        AddInfoRow(col, "Date . . . . . . . . . . :", invoice.InvoiceDate.ToString("yy-MM-dd"));
        AddInfoRow(col, "Buyer no . . . . . . :", invoice.Buyer?.BuyerNumber ?? "");
        AddInfoRow(col, "VAT no. . . . . . . . :", invoice.Buyer?.VatRegistrationNo ?? "");
        if (invoice.OriginalInvoice != null)
            AddInfoRow(col, "Ref. invoice . . . :", invoice.OriginalInvoice.InvoiceNumber);
        if (invoice.PromptDate.HasValue)
            AddInfoRow(col, "Prompt date . . . :", invoice.PromptDate.Value.ToString("yy-MM-dd"));
    }

    private static void AddInfoRow(ColumnDescriptor col, string label, string value)
    {
        col.Item().Row(r =>
        {
            r.RelativeItem().Text(label).FontSize(8);
            r.ConstantItem(100).AlignRight().Text(value).FontSize(8);
        });
    }

    private static void ComposeContent(IContainer container, Invoice invoice)
    {
        container.Column(col =>
        {
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(2);   // Lot no
                    c.RelativeColumn(1.2f); // No. of skins
                    c.RelativeColumn(4);   // Type, Description
                    c.RelativeColumn(2);   // Per skin EUR
                    c.RelativeColumn(2.5f); // Total EUR
                });

                table.Header(header =>
                {
                    header.Cell().BorderBottom(1).Padding(4).Text("Lot no.").Bold().FontSize(8);
                    header.Cell().BorderBottom(1).Padding(4).Text("No. of skins").Bold().FontSize(8);
                    header.Cell().BorderBottom(1).Padding(4).Text("Type, Description, size, quality").Bold().FontSize(8);
                    header.Cell().BorderBottom(1).Padding(4).AlignRight().Text("Hammer price EUR\nPer skin").Bold().FontSize(8);
                    header.Cell().BorderBottom(1).Padding(4).AlignRight().Text("Hammer price EUR\nTotal").Bold().FontSize(8);
                });

                foreach (var line in invoice.Lines)
                {
                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(line.LotNumber.ToString());
                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).AlignRight().Text(line.Skins.ToString("N0"));
                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(line.Description);
                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).AlignRight().Text(FormatAmount(line.PricePerSkin));
                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).AlignRight().Text(FormatAmount(line.HammerPrice));
                }

                // Subtotal row
                var totalSkins = invoice.Lines.Sum(l => l.Skins);
                table.Cell().Padding(3).Text("");
                table.Cell().BorderTop(1).Padding(3).AlignRight().Text(totalSkins.ToString("N0")).Bold();
                table.Cell().BorderTop(1).Padding(3).Text("");
                table.Cell().BorderTop(1).Padding(3).Text("");
                table.Cell().BorderTop(1).Padding(3).AlignRight().Text(FormatAmount(invoice.SubTotal)).Bold();
            });

            col.Item().Height(10);

            // Auction fee
            col.Item().Row(row =>
            {
                row.RelativeItem().Text("");
                row.ConstantItem(250).Row(inner =>
                {
                    inner.RelativeItem().Text("Auction fee").FontSize(9);
                    inner.ConstantItem(40).Text("EUR").FontSize(9);
                    inner.ConstantItem(100).AlignRight().Text(FormatAmount(invoice.AuctionFee)).FontSize(9);
                });
            });

            col.Item().Height(5);

            // Subtotal after auction fee
            col.Item().Row(row =>
            {
                row.RelativeItem().Text("");
                row.ConstantItem(100).AlignRight().Text(FormatAmount(invoice.SubTotal + invoice.AuctionFee)).Bold().FontSize(10);
            });

            col.Item().Height(10);

            // Commission
            col.Item().Row(row =>
            {
                row.RelativeItem().Text("");
                row.ConstantItem(250).Row(inner =>
                {
                    inner.RelativeItem().Text("Commission on total").FontSize(9);
                    inner.ConstantItem(40).Text("EUR").FontSize(9);
                    inner.ConstantItem(100).AlignRight().Text(FormatAmount(invoice.Commission)).FontSize(9);
                });
            });

            col.Item().Height(10);

            // Total
            col.Item().Row(row =>
            {
                row.RelativeItem().Text("");
                row.ConstantItem(250).BorderTop(2).PaddingTop(5).Row(inner =>
                {
                    inner.RelativeItem().Text("Total Amount ex warehouse").Bold().FontSize(10);
                    inner.ConstantItem(40).Text("EUR").Bold().FontSize(10);
                    inner.ConstantItem(100).AlignRight().Text(FormatAmount(invoice.TotalAmount)).Bold().FontSize(10);
                });
            });
        });
    }

    private static void ComposeFooter(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().BorderTop(1).PaddingTop(5);
            col.Item().Text("Please refer to Kopenhagen Fur's Conditions of Sale on www.kopenhagenfur.com")
                .FontSize(7).Italic();
        });
    }

    private static string FormatAmount(decimal amount)
    {
        return amount.ToString("N2");
    }
}
