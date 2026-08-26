using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace KSeF.Functions.Services;

// Direct-download PDF wizualizacja of a KSeF FA(3)/FA(2) invoice XML — same content as the
// HTML viewer (parties, lines, per-rate VAT summary, payment, verification QR), rendered
// with QuestPDF like the auction system's other PDF documents.
public static class KSeFInvoicePdfService
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    public static byte[] Render(string ksefNumber, string xml)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var doc = XDocument.Parse(xml);
        var root = doc.Root!;
        XElement? El(XElement? scope, string name) =>
            scope?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        string Val(XElement? scope, params string[] path)
        {
            var cur = scope;
            foreach (var p in path) { cur = El(cur, p); if (cur == null) return ""; }
            return cur?.Value?.Trim() ?? "";
        }
        string Money(string v) => decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d.ToString("N2", Pl) : v;

        var fa = El(root, "Fa");
        var rodzaj = Val(fa, "RodzajFaktury");
        var docKind = rodzaj switch
        {
            "VAT" => "Faktura VAT", "KOR" => "Faktura korygująca", "ZAL" => "Faktura zaliczkowa",
            "ROZ" => "Faktura rozliczeniowa", "UPR" => "Faktura uproszczona",
            "KOR_ZAL" => "Korekta faktury zaliczkowej", "KOR_ROZ" => "Korekta faktury rozliczeniowej",
            _ => "Faktura"
        };
        var currency = Val(fa, "KodWaluty");

        List<string> Party(XElement? p)
        {
            var lines = new List<string>();
            if (p == null) return lines;
            var ident = El(p, "DaneIdentyfikacyjne");
            var name = Val(ident, "Nazwa");
            if (name != "") lines.Add(name);
            var nip = ident?.Elements().FirstOrDefault(e => e.Name.LocalName is "NIP" or "NrVatUE")?.Value;
            if (!string.IsNullOrEmpty(nip)) lines.Add("NIP/VAT: " + nip);
            var adres = El(p, "Adres");
            if (adres != null)
                lines.AddRange(adres.Elements().Where(e => e.Name.LocalName.StartsWith("AdresL")).Select(e => e.Value));
            return lines;
        }
        var seller = Party(El(root, "Podmiot1"));
        var buyer = Party(El(root, "Podmiot2"));

        var rows = (fa?.Elements().Where(e => e.Name.LocalName == "FaWiersz") ?? Enumerable.Empty<XElement>())
            .Select(w => new
            {
                Nr = Val(w, "NrWierszaFa"), Name = Val(w, "P_7"), Qty = Val(w, "P_8B"), Unit = Val(w, "P_8A"),
                UnitNet = Val(w, "P_9A"), Net = Val(w, "P_11"), Gross = Val(w, "P_11A"), Vat = Val(w, "P_12")
            }).ToList();

        var rateLabels = new Dictionary<string, string>
        {
            ["1"] = "23% / 22%", ["2"] = "8% / 7%", ["3"] = "5%", ["4"] = "ryczałt taxi",
            ["5"] = "proc. szczególna", ["6_1"] = "0% krajowe", ["6_2"] = "0% WDT", ["6_3"] = "0% eksport",
            ["7"] = "zw", ["8"] = "np", ["9"] = "np art. 100", ["10"] = "WDT nowe śr. transp.", ["11"] = "marża"
        };
        var vatRows = new List<(string Label, string Net, string Vat)>();
        foreach (var el in fa?.Elements() ?? Enumerable.Empty<XElement>())
        {
            if (!el.Name.LocalName.StartsWith("P_13_")) continue;
            var suffix = el.Name.LocalName.Substring(5);
            var vat = fa!.Elements().FirstOrDefault(e => e.Name.LocalName == "P_14_" + suffix)?.Value ?? "";
            vatRows.Add((rateLabels.TryGetValue(suffix, out var l) ? l : el.Name.LocalName, el.Value, vat));
        }

        var platnosc = El(fa, "Platnosc");
        var forma = Val(platnosc, "FormaPlatnosci") switch
        {
            "1" => "gotówka", "2" => "karta", "3" => "bon", "4" => "czek",
            "5" => "kredyt", "6" => "przelew", "7" => "mobilna", var o => o
        };
        var termin = platnosc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Termin")?.Value ?? "";
        var rachunek = platnosc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NrRB")?.Value ?? "";

        var korekta = fa?.Descendants().FirstOrDefault(e => e.Name.LocalName == "DaneFaKorygowanej");

        var xmlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();
        var verifyUrl = $"https://ksef.mf.gov.pl/web/verify/{Uri.EscapeDataString(ksefNumber)}/{xmlHash}";
        byte[] qrPng;
        using (var qrGen = new QRCodeGenerator())
        using (var qrData = qrGen.CreateQrCode(verifyUrl, QRCodeGenerator.ECCLevel.Q))
            qrPng = new PngByteQRCode(qrData).GetGraphic(6);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(8.5f));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text($"{docKind} {Val(fa, "P_2")}").FontSize(15).Bold();
                            var meta = $"Data wystawienia: {Val(fa, "P_1")}";
                            var p6 = Val(fa, "P_6");
                            if (p6 != "") meta += $"   ·   Data sprzedaży: {p6}";
                            if (currency != "") meta += $"   ·   Waluta: {currency}";
                            c.Item().Text(meta).FontColor(Colors.Grey.Darken1);
                        });
                        row.ConstantItem(190).AlignRight().Column(c =>
                        {
                            c.Item().AlignRight().Text("Numer KSeF").FontColor(Colors.Grey.Darken1).FontSize(7.5f);
                            c.Item().AlignRight().Text(ksefNumber).FontSize(8).Bold();
                        });
                    });
                    col.Item().PaddingTop(6).LineHorizontal(0.75f).LineColor(Colors.Grey.Lighten1);
                });

                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        void PartyBox(RowDescriptor r, string title, List<string> lines)
                        {
                            r.RelativeItem().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(c =>
                            {
                                c.Item().Text(title).FontSize(7).FontColor(Colors.Grey.Darken1).Bold();
                                foreach (var l in lines) c.Item().Text(l);
                            });
                        }
                        PartyBox(row, "SPRZEDAWCA", seller);
                        row.ConstantItem(12);
                        PartyBox(row, "NABYWCA", buyer);
                    });

                    if (korekta != null)
                        col.Item().PaddingTop(8).Text(t =>
                        {
                            t.Span("Koryguje fakturę: ").Bold();
                            t.Span($"{Val(korekta, "NrFaKorygowanej")} z {Val(korekta, "DataWystFaKorygowanej")}");
                            var orig = Val(korekta, "NrKSeFFaKorygowanej");
                            if (orig != "") t.Span($" (KSeF {orig})");
                        });

                    col.Item().PaddingTop(12).Table(table =>
                    {
                        table.ColumnsDefinition(cd =>
                        {
                            cd.ConstantColumn(18); cd.RelativeColumn(5); cd.ConstantColumn(40); cd.ConstantColumn(28);
                            cd.ConstantColumn(55); cd.ConstantColumn(60); cd.ConstantColumn(60); cd.ConstantColumn(30);
                        });
                        IContainer Head(IContainer c) => c.Background(Colors.Grey.Lighten3)
                            .BorderBottom(0.75f).BorderColor(Colors.Grey.Medium).Padding(4);
                        table.Header(h =>
                        {
                            h.Cell().Element(Head).Text("#").Bold();
                            h.Cell().Element(Head).Text("Nazwa towaru / usługi").Bold();
                            h.Cell().Element(Head).AlignRight().Text("Ilość").Bold();
                            h.Cell().Element(Head).Text("Jm").Bold();
                            h.Cell().Element(Head).AlignRight().Text("Cena netto").Bold();
                            h.Cell().Element(Head).AlignRight().Text("Wartość netto").Bold();
                            h.Cell().Element(Head).AlignRight().Text("Wartość brutto").Bold();
                            h.Cell().Element(Head).Text("VAT").Bold();
                        });
                        IContainer Cell(IContainer c) => c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4);
                        foreach (var r in rows)
                        {
                            table.Cell().Element(Cell).Text(r.Nr);
                            table.Cell().Element(Cell).Text(r.Name);
                            table.Cell().Element(Cell).AlignRight().Text(r.Qty);
                            table.Cell().Element(Cell).Text(r.Unit);
                            table.Cell().Element(Cell).AlignRight().Text(Money(r.UnitNet));
                            table.Cell().Element(Cell).AlignRight().Text(Money(r.Net));
                            table.Cell().Element(Cell).AlignRight().Text(Money(r.Gross));
                            table.Cell().Element(Cell).Text(r.Vat);
                        }
                    });

                    col.Item().PaddingTop(12).Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            if (forma != "" || termin != "" || rachunek != "")
                            {
                                c.Item().Text("PŁATNOŚĆ").FontSize(7).FontColor(Colors.Grey.Darken1).Bold();
                                if (forma != "") c.Item().Text($"Forma: {forma}");
                                if (termin != "") c.Item().Text($"Termin płatności: {termin}");
                                if (rachunek != "") c.Item().Text($"Rachunek: {rachunek}");
                            }
                        });
                        row.ConstantItem(230).Column(c =>
                        {
                            if (vatRows.Count > 0)
                                c.Item().Table(vt =>
                                {
                                    vt.ColumnsDefinition(cd => { cd.RelativeColumn(2); cd.RelativeColumn(); cd.RelativeColumn(); });
                                    IContainer VC(IContainer x) => x.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);
                                    vt.Header(h =>
                                    {
                                        h.Cell().Element(VC).Text("Stawka VAT").Bold();
                                        h.Cell().Element(VC).AlignRight().Text("Netto").Bold();
                                        h.Cell().Element(VC).AlignRight().Text("VAT").Bold();
                                    });
                                    foreach (var (label, net, vat) in vatRows)
                                    {
                                        vt.Cell().Element(VC).Text(label);
                                        vt.Cell().Element(VC).AlignRight().Text(Money(net));
                                        vt.Cell().Element(VC).AlignRight().Text(Money(vat));
                                    }
                                });
                            c.Item().PaddingTop(8).AlignRight().Text(t =>
                            {
                                t.Span("Do zapłaty: ").FontSize(11);
                                t.Span($"{Money(Val(fa, "P_15"))} {currency}".Trim()).FontSize(13).Bold();
                            });
                        });
                    });

                    col.Item().PaddingTop(18).Row(row =>
                    {
                        row.ConstantItem(80).Image(qrPng);
                        row.ConstantItem(10);
                        row.RelativeItem().AlignMiddle().Column(c =>
                        {
                            c.Item().Text("KOD QR — weryfikacja faktury w KSeF").Bold().FontSize(8);
                            c.Item().Text(ksefNumber).FontSize(7.5f);
                            c.Item().Text(verifyUrl).FontSize(6.5f).FontColor(Colors.Grey.Darken1);
                        });
                    });
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                    t.Span("Wizualizacja faktury ustrukturyzowanej (FA) z KSeF   ·   strona ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();
    }
}
