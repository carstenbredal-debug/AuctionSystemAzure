using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KSeF.Functions.Models;

namespace KSeF.Functions.Services;

public class JpkV7MBuilder
{
    private static readonly XNamespace Ns = "http://crd.gov.pl/wzor/2025/12/19/14090/";
    private static readonly XNamespace Etd = "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/09/13/eD/DefinicjeTypy/";

    public (string xml, int salesCount, decimal taxDue) Build(JpkV7MRequest req)
    {
        var root = new XElement(Ns + "JPK",
            new XAttribute(XNamespace.Xmlns + "tns", Ns.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "etd", Etd.NamespaceName)
        );

        // Naglowek (Header)
        root.Add(BuildNaglowek(req));

        // Podmiot1 (Taxpayer)
        root.Add(BuildPodmiot1(req.Company));

        // Deklaracja (Declaration)
        var (deklaracja, taxDue) = BuildDeklaracja(req);
        root.Add(deklaracja);

        // Ewidencja (Records)
        var (ewidencja, salesCount) = BuildEwidencja(req);
        root.Add(ewidencja);

        // Serialize to string with UTF-8
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            OmitXmlDeclaration = false
        };

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            root.Save(writer);
        }

        var xml = Encoding.UTF8.GetString(ms.ToArray());
        return (xml, salesCount, taxDue);
    }

    private XElement BuildNaglowek(JpkV7MRequest req)
    {
        var celZlozenia = req.Purpose == "Correction" ? 2 : 1;

        return new XElement(Ns + "Naglowek",
            new XElement(Ns + "KodFormularza",
                new XAttribute("kodSystemowy", "JPK_V7M (3)"),
                new XAttribute("wersjaSchemy", "1-0E"),
                "JPK_VAT"
            ),
            new XElement(Ns + "WariantFormularza", 3),
            new XElement(Ns + "DataWytworzeniaJPK", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
            new XElement(Ns + "NazwaSystemu", "KPHG KSeF Integration"),
            new XElement(Ns + "CelZlozenia",
                new XAttribute("poz", "P_7"),
                celZlozenia
            ),
            new XElement(Ns + "KodUrzedu", "0271"),
            new XElement(Ns + "Rok", req.Year),
            new XElement(Ns + "Miesiac", req.Month)
        );
    }

    private XElement BuildPodmiot1(CompanyData company)
    {
        var osoba = new XElement(Ns + "OsobaNiefizyczna",
            new XElement(Ns + "NIP", company.Nip),
            new XElement(Ns + "PelnaNazwa", company.Name),
            new XElement(Ns + "Email", string.IsNullOrWhiteSpace(company.Email) ? "biuro@firma.pl" : company.Email)
        );

        if (!string.IsNullOrWhiteSpace(company.Phone))
            osoba.Add(new XElement(Ns + "Telefon", company.Phone));

        return new XElement(Ns + "Podmiot1",
            new XAttribute("rola", "Podatnik"),
            osoba);
    }

    // Net-base boxes summed into P_37; output-VAT boxes summed into P_38 (per the schema).
    private static readonly int[] NetBaseBoxes = { 10, 11, 13, 15, 17, 19, 21, 22 };
    private static readonly int[] OutputVatBoxes = { 16, 18, 20 };

    // Expand a sale record into its per-category VAT components (single fallback for back-compat).
    private static IEnumerable<SalesVatBreakdown> Components(SalesRecordData rec)
    {
        if (rec.Vat is { Count: > 0 })
            return rec.Vat;
        return new[]
        {
            new SalesVatBreakdown
            {
                Category = rec.VatAmount != 0m ? "STD" : "ZW",
                NetAmount = rec.NetAmount,
                VatAmount = rec.VatAmount
            }
        };
    }

    // Add a component's amounts to the K_/P_ box accumulator (box number -> signed amount).
    // Category drives the box per the VAT Bus. Posting Group rules:
    //   STD -> domestic rated (K_19/20, K_17/18, K_15/16 by rate);  ZW -> exempt (K_10);
    //   OO  -> intra-EU reverse-charge (K_11 + K_12);                NP -> outside-scope (K_11).
    private static void Accumulate(SalesVatBreakdown c, decimal sign, IDictionary<int, decimal> boxes)
    {
        void Add(int box, decimal v) { boxes.TryGetValue(box, out var cur); boxes[box] = cur + v; }
        var net = Math.Abs(c.NetAmount);
        var vat = Math.Abs(c.VatAmount);
        switch (c.Category?.Trim().ToUpperInvariant())
        {
            case "ZW": Add(10, net * sign); break;
            case "OO": Add(11, net * sign); Add(12, net * sign); break; // K_12 is a subset of K_11
            case "NP": Add(11, net * sign); break;
            default:
                var rate = net != 0m ? Math.Round(vat / net * 100m, 0) : 0m;
                if (rate >= 22m) { Add(19, net * sign); Add(20, vat * sign); }
                else if (rate >= 7m) { Add(17, net * sign); Add(18, vat * sign); }
                else if (rate >= 4m) { Add(15, net * sign); Add(16, vat * sign); }
                else { Add(13, net * sign); } // 0% domestic
                break;
        }
    }

    private (XElement deklaracja, decimal taxDue) BuildDeklaracja(JpkV7MRequest req)
    {
        var boxes = new SortedDictionary<int, decimal>();
        foreach (var rec in req.SalesRecords)
        {
            var sign = rec.IsCreditMemo ? -1m : 1m;
            foreach (var c in Components(rec)) Accumulate(c, sign, boxes);
        }
        decimal Box(int n) { boxes.TryGetValue(n, out var v); return v; }

        var totalNet = NetBaseBoxes.Sum(Box);      // P_37 (total base)
        var totalTaxDue = OutputVatBoxes.Sum(Box); // P_38 (total output VAT)

        var pozycje = new XElement(Ns + "PozycjeSzczegolowe",
            new XElement(Ns + "P_10", FmtInt(Box(10))),
            new XElement(Ns + "P_11", FmtInt(Box(11))),
            new XElement(Ns + "P_12", FmtInt(Box(12))),
            new XElement(Ns + "P_13", FmtInt(Box(13))),
            new XElement(Ns + "P_14", FmtInt(0)),
            new XElement(Ns + "P_15", FmtInt(Box(15))),
            new XElement(Ns + "P_16", FmtInt(Box(16))),
            new XElement(Ns + "P_17", FmtInt(Box(17))),
            new XElement(Ns + "P_18", FmtInt(Box(18))),
            new XElement(Ns + "P_19", FmtInt(Box(19))),
            new XElement(Ns + "P_20", FmtInt(Box(20))),
            new XElement(Ns + "P_21", FmtInt(Box(21))),
            new XElement(Ns + "P_22", FmtInt(Box(22))),
            new XElement(Ns + "P_23", FmtInt(0)),
            new XElement(Ns + "P_24", FmtInt(0)),
            new XElement(Ns + "P_25", FmtInt(0)),
            new XElement(Ns + "P_26", FmtInt(0)),
            new XElement(Ns + "P_27", FmtInt(0)),
            new XElement(Ns + "P_28", FmtInt(0)),
            new XElement(Ns + "P_29", FmtInt(0)),
            new XElement(Ns + "P_30", FmtInt(0)),
            new XElement(Ns + "P_31", FmtInt(0)),
            new XElement(Ns + "P_32", FmtInt(0)),
            new XElement(Ns + "P_33", FmtInt(0)),
            new XElement(Ns + "P_34", FmtInt(0)),
            new XElement(Ns + "P_35", FmtInt(0)),
            new XElement(Ns + "P_36", FmtInt(0)),
            new XElement(Ns + "P_37", FmtInt(totalNet)),
            new XElement(Ns + "P_38", FmtInt(totalTaxDue)),
            new XElement(Ns + "P_39", FmtInt(0)),
            new XElement(Ns + "P_40", FmtInt(0)),
            new XElement(Ns + "P_41", FmtInt(0)),
            new XElement(Ns + "P_42", FmtInt(0)),
            new XElement(Ns + "P_43", FmtInt(0)),
            new XElement(Ns + "P_44", FmtInt(0)),
            new XElement(Ns + "P_45", FmtInt(0)),
            new XElement(Ns + "P_46", FmtInt(0)),
            new XElement(Ns + "P_47", FmtInt(0)),
            new XElement(Ns + "P_48", FmtInt(0)),
            new XElement(Ns + "P_49", FmtInt(0)),
            new XElement(Ns + "P_50", FmtInt(0)),
            new XElement(Ns + "P_51", FmtInt(totalTaxDue)),
            new XElement(Ns + "P_52", FmtInt(0)),
            new XElement(Ns + "P_53", FmtInt(0)),
            new XElement(Ns + "P_54", FmtInt(0)),
            // P_540/P_55/P_56/P_560/P_58 is a choice — pick one refund term
            new XElement(Ns + "P_540", 1),
            // P_59/P_60/P_61 optional group (credit to future obligations) — omitted
            new XElement(Ns + "P_68", FmtInt(0)),
            new XElement(Ns + "P_69", FmtInt(0))
        );

        var deklaracja = new XElement(Ns + "Deklaracja",
            new XElement(Ns + "Naglowek",
                new XElement(Ns + "KodFormularzaDekl",
                    new XAttribute("kodSystemowy", "VAT-7 (23)"),
                    new XAttribute("kodPodatku", "VAT"),
                    new XAttribute("rodzajZobowiazania", "Z"),
                    new XAttribute("wersjaSchemy", "1-0E"),
                    "VAT-7"
                ),
                new XElement(Ns + "WariantFormularzaDekl", 23)
            ),
            pozycje,
            new XElement(Ns + "Pouczenia", 1)
        );

        return (deklaracja, totalTaxDue);
    }

    private (XElement ewidencja, int salesCount) BuildEwidencja(JpkV7MRequest req)
    {
        var ewidencja = new XElement(Ns + "Ewidencja");
        int lineNo = 0;
        decimal totalOutputVat = 0;

        foreach (var rec in req.SalesRecords)
        {
            lineNo++;
            var wiersz = new XElement(Ns + "SprzedazWiersz",
                new XElement(Ns + "LpSprzedazy", lineNo)
            );

            // KodKrajuNadaniaTIN must come before NrKontrahenta per schema
            if (!string.IsNullOrWhiteSpace(rec.CountryCode) && rec.CountryCode != "PL")
                wiersz.Add(new XElement(Ns + "KodKrajuNadaniaTIN", rec.CountryCode));

            wiersz.Add(new XElement(Ns + "NrKontrahenta", string.IsNullOrWhiteSpace(rec.CounterpartyNip) ? "BRAK" : rec.CounterpartyNip));
            wiersz.Add(new XElement(Ns + "NazwaKontrahenta", rec.CounterpartyName));
            wiersz.Add(new XElement(Ns + "DowodSprzedazy", rec.InvoiceNumber));
            wiersz.Add(new XElement(Ns + "DataWystawienia", rec.IssueDate));

            if (!string.IsNullOrWhiteSpace(rec.SaleDate))
                wiersz.Add(new XElement(Ns + "DataSprzedazy", rec.SaleDate));

            // KSeF choice (required in v3): NrKSeF, OFF, BFK, or DI
            if (!string.IsNullOrEmpty(rec.KSeFReferenceNumber))
                wiersz.Add(new XElement(Ns + "NrKSeF", rec.KSeFReferenceNumber));
            else
                wiersz.Add(new XElement(Ns + "BFK", 1));

            // K_ boxes per category component (one row may span several boxes for a mixed invoice),
            // emitted in ascending (schema) order.
            var sign = rec.IsCreditMemo ? -1m : 1m;
            var boxes = new SortedDictionary<int, decimal>();
            foreach (var c in Components(rec)) Accumulate(c, sign, boxes);
            foreach (var kv in boxes)
                if (kv.Value != 0m)
                    wiersz.Add(new XElement(Ns + $"K_{kv.Key}", Fmt(kv.Value)));

            boxes.TryGetValue(16, out var v16);
            boxes.TryGetValue(18, out var v18);
            boxes.TryGetValue(20, out var v20);
            totalOutputVat += v16 + v18 + v20;
            ewidencja.Add(wiersz);
        }

        // SprzedazCtrl
        ewidencja.Add(new XElement(Ns + "SprzedazCtrl",
            new XElement(Ns + "LiczbaWierszySprzedazy", lineNo),
            new XElement(Ns + "PodatekNalezny", Fmt(totalOutputVat))
        ));

        // ZakupCtrl (no purchases)
        ewidencja.Add(new XElement(Ns + "ZakupCtrl",
            new XElement(Ns + "LiczbaWierszyZakupow", 0),
            new XElement(Ns + "PodatekNaliczony", Fmt(0))
        ));

        return (ewidencja, lineNo);
    }

    private static string Fmt(decimal value) =>
        value.ToString("F2", CultureInfo.InvariantCulture);

    private static string FmtInt(decimal value) =>
        Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture);
}
