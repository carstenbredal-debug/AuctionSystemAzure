using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AuctionSystem.Functions.Services;

// Grading fee note for one farmer: sold skins x the farmer's contractual grading fee, with
// VAT per the farmer's VAT group. Same BC-invoice-style layout as the sales notes.
// Presentation only — the fee itself is posted in BC.
public static class GradingFeeNotesPdfService
{
    private static readonly CultureInfo Da = CultureInfo.GetCultureInfo("da-DK");
    private static string Amt(decimal v) => v.ToString("N2", Da);

    public sealed class FarmerFeeNote
    {
        public string FarmerNumber = "";
        public string Name = "";
        public string? Name2;
        public string? AddressLine1;
        public string? AddressLine2;
        public string? PostalCode;
        public string? City;
        public string? Country;
        public string? VatRegistrationNo;
        public decimal VatRate;
        public int SoldSkins;
        public decimal FeePerSkin;
    }

    public static byte[] Generate(FarmerFeeNote note, string auctionNumber, DateTime date)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var net = note.SoldSkins * note.FeePerSkin;
        var vat = net * note.VatRate;
        var gross = net + vat;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginTop(35);
                page.MarginBottom(35);
                page.MarginLeft(45);
                page.MarginRight(45);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem(6).Column(left =>
                        {
                            left.Item().Text("KOPENHAGEN\nFUR").Bold().FontSize(13);
                            left.Item().Height(18);
                            left.Item().Text(note.Name).FontSize(10);
                            foreach (var line in new[]
                                     {
                                         note.Name2, note.AddressLine1, note.AddressLine2,
                                         string.Join(" ", new[] { note.PostalCode, note.City }.Where(s => !string.IsNullOrEmpty(s))),
                                         note.Country
                                     }.Where(l => !string.IsNullOrEmpty(l)))
                                left.Item().Text(line!).FontSize(10);
                        });
                        row.RelativeItem(4).Column(right =>
                        {
                            right.Item().Text("GRADING FEE NOTE").Bold().FontSize(16);
                            right.Item().Height(14);
                            right.Item().Text("KPHG (Holland) Coöperatief U.A.").FontSize(9);
                            right.Item().Text("Sienna 39").FontSize(9);
                            right.Item().Text("00-121 Warszawa").FontSize(9);
                            right.Item().Text("PL").FontSize(8);
                            right.Item().Height(10);
                            InfoRow(right, "VAT Registration No.", "5253073718", false);
                            right.Item().Height(8);
                            InfoRow(right, "Note number", $"GF-{auctionNumber}-{note.FarmerNumber}", true);
                            InfoRow(right, "Date", date.ToString("dd-MM-yy"), true);
                            InfoRow(right, "Farmer no.", note.FarmerNumber, true);
                            if (!string.IsNullOrEmpty(note.VatRegistrationNo))
                                InfoRow(right, "Farmer VAT No.", note.VatRegistrationNo!, true);
                        });
                    });
                    col.Item().Height(25);
                });

                page.Content().Column(col =>
                {
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(5.5f);
                            c.RelativeColumn(2);
                            c.RelativeColumn(2);
                            c.RelativeColumn(2.5f);
                        });

                        table.Header(header =>
                        {
                            header.Cell().BorderBottom(1).Padding(4).Text("Description").Bold().FontSize(8);
                            header.Cell().BorderBottom(1).Padding(4).AlignRight().Text("No. of skins").Bold().FontSize(8);
                            header.Cell().BorderBottom(1).Padding(4).AlignRight().Text("Grading fee").Bold().FontSize(8);
                            header.Cell().BorderBottom(1).Padding(4).AlignRight().Text("Grading fee Total").Bold().FontSize(8);
                        });

                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4)
                            .Text($"{note.SoldSkins.ToString("N0", Da)} skins x {Amt(note.FeePerSkin)} EUR grading fee — auction {auctionNumber}");
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4)
                            .AlignRight().Text(note.SoldSkins.ToString("N0", Da));
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4)
                            .AlignRight().Text(Amt(note.FeePerSkin));
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4)
                            .AlignRight().Text(Amt(net));
                    });

                    col.Item().Height(25);

                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Row(r =>
                        {
                            r.ConstantItem(80).Text("Total Skins").Bold().FontSize(9);
                            r.ConstantItem(80).AlignRight().Text(note.SoldSkins.ToString("N0", Da)).Bold().FontSize(9);
                        });
                        row.ConstantItem(280).Column(totals =>
                        {
                            TotalRow(totals, "Amount", Amt(net), false, false);
                            TotalRow(totals, "SubTotal", Amt(net), true, true);
                            TotalRow(totals, "VAT", Amt(vat), false, false);
                            TotalRow(totals, "TOTAL", Amt(gross), true, true);
                        });
                    });
                });

                page.Footer().Column(col =>
                {
                    col.Item().Text($"Grading fee: EUR {Amt(note.FeePerSkin)} per sold skin. Please Refer to Kopenhagen Furs Condition of Sales on www.kopenhagenfur.com")
                        .FontSize(7).Italic();
                    col.Item().Height(4);
                    col.Item().Text("Grading Fee Note Version 2026-1").FontSize(6);
                });
            });
        });

        using var stream = new MemoryStream();
        document.GeneratePdf(stream);
        return stream.ToArray();
    }

    private static void InfoRow(ColumnDescriptor col, string label, string value, bool bold)
    {
        col.Item().Row(r =>
        {
            var lbl = r.RelativeItem().Text(label).FontSize(9);
            if (bold) lbl.Bold();
            var val = r.ConstantItem(110).AlignRight().Text(value).FontSize(9);
            if (bold) val.Bold();
        });
    }

    private static void TotalRow(ColumnDescriptor col, string label, string amount, bool bold, bool topBorder)
    {
        col.Item().Element(e => topBorder ? e.BorderTop(1).PaddingTop(3) : e).Row(r =>
        {
            var l = r.RelativeItem().Text(label).FontSize(9);
            var c = r.ConstantItem(40).Text("EUR").FontSize(9);
            var a = r.ConstantItem(90).AlignRight().Text(amount).FontSize(9);
            if (bold) { l.Bold(); c.Bold(); a.Bold(); }
        });
        col.Item().Height(4);
    }
}
