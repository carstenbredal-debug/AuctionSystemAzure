using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KSeF.Functions.Models;

namespace KSeF.Functions.Services;

/// <summary>
/// Builds KSeF-compliant FA(3) XML invoice documents.
/// Schema: http://crd.gov.pl/wzor/2025/06/25/13775/
/// FA(3) is mandatory for all KSeF submissions from 2026-02-01; FA(2) is no longer accepted.
/// </summary>
public class InvoiceXmlBuilder
{
    private static readonly XNamespace Ns = "http://crd.gov.pl/wzor/2025/06/25/13775/";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public string Build(InvoiceData invoice)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            BuildFaktura(invoice)
        );

        using var ms = new MemoryStream();
        using var xw = XmlWriter.Create(ms, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true
        });
        doc.Save(xw);
        xw.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private XElement BuildFaktura(InvoiceData inv)
    {
        var faktura = new XElement(Ns + "Faktura",
            new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
            new XAttribute(Xsi + "schemaLocation", "http://crd.gov.pl/wzor/2025/06/25/13775/ http://crd.gov.pl/wzor/2025/06/25/13775/schemat.xsd"),

            // Naglowek (Header)
            BuildNaglowek(inv),

            // Podmiot1 (Seller)
            BuildPodmiot1(inv.Seller),

            // Podmiot2 (Buyer)
            BuildPodmiot2(inv.Buyer),

            // Fa (Invoice body)
            BuildFa(inv)
        );

        return faktura;
    }

    private XElement BuildNaglowek(InvoiceData inv)
    {
        return new XElement(Ns + "Naglowek",
            new XElement(Ns + "KodFormularza",
                new XAttribute("kodSystemowy", "FA (3)"),
                new XAttribute("wersjaSchemy", "1-0E"),
                "FA"),
            new XElement(Ns + "WariantFormularza", 3),
            new XElement(Ns + "DataWytworzeniaFa", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement(Ns + "SystemInfo", "KSeFIntegration/1.0")
        );
    }

    private XElement BuildPodmiot1(SellerData seller)
    {
        var countryCode = string.IsNullOrWhiteSpace(seller.CountryCode) ? "PL" : seller.CountryCode;
        return new XElement(Ns + "Podmiot1",
            new XElement(Ns + "DaneIdentyfikacyjne",
                new XElement(Ns + "NIP", CleanNip(seller.NIP)),
                new XElement(Ns + "Nazwa", seller.Name)
            ),
            new XElement(Ns + "Adres",
                new XElement(Ns + "KodKraju", IsoCountry(countryCode)),
                new XElement(Ns + "AdresL1", FormatAddress(seller.Street, seller.BuildingNumber, seller.ApartmentNumber)),
                new XElement(Ns + "AdresL2", $"{seller.PostalCode} {seller.City}")
            )
        );
    }

    private static readonly HashSet<string> EuCountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
        "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
        "PT", "RO", "SK", "SI", "ES", "SE"
    };

    // KSeF KodKraju (address country) requires the ISO 3166-1 alpha-2 code. Greece's EU VAT prefix is "EL"
    // (which is what our data stores), but its ISO country code is "GR" — KSeF's TKodKraju rejects "EL".
    // Map it for KodKraju only; KodUE (the VAT-prefix field) correctly keeps "EL".
    private static string IsoCountry(string countryCode)
        => string.Equals(countryCode, "EL", StringComparison.OrdinalIgnoreCase) ? "GR" : countryCode;

    private enum ZeroRateKind { Domestic, IntraEu, Export }

    // FA(3) zero-rate classification from the buyer's country: PL -> domestic (0 KR),
    // other EU -> intra-EU supply (0 WDT), rest -> export (0 EX).
    private static ZeroRateKind ClassifyZeroRate(BuyerData buyer)
    {
        var cc = string.IsNullOrWhiteSpace(buyer.CountryCode) ? "PL" : buyer.CountryCode.Trim().ToUpperInvariant();
        if (cc == "PL") return ZeroRateKind.Domestic;
        return EuCountryCodes.Contains(IsoCountry(cc)) ? ZeroRateKind.IntraEu : ZeroRateKind.Export;
    }

    // FA(3) tax categories. The line VAT% cannot distinguish these (0% / WDT / export /
    // exempt / reverse-charge all have VAT% = 0), so BC supplies the category per line.
    private enum TaxCat { Standard, ZeroDomestic, ZeroIntraEu, ZeroExport, Exempt, ReverseChargeEu }

    // Resolve a line's category from the BC-supplied code, falling back to VatRate + buyer.
    private static TaxCat ResolveCategory(InvoiceLineData line, ZeroRateKind zeroKind)
    {
        switch (line.TaxCategory?.Trim().ToUpperInvariant())
        {
            case "STD": return TaxCat.Standard;
            case "KR": return TaxCat.ZeroDomestic;
            case "WDT": return TaxCat.ZeroIntraEu;
            case "EXP":
            case "EX": return TaxCat.ZeroExport;
            case "ZW": return TaxCat.Exempt;
            case "OO": return TaxCat.ReverseChargeEu;
            default:
                // No (or unknown) category supplied: infer from rate + buyer country.
                if (line.VatRate > 0m) return TaxCat.Standard;
                return zeroKind switch
                {
                    ZeroRateKind.IntraEu => TaxCat.ZeroIntraEu,
                    ZeroRateKind.Export => TaxCat.ZeroExport,
                    _ => TaxCat.ZeroDomestic
                };
        }
    }

    // Line-level P_12 code for a category, matching its summary bucket.
    private static string LineVatCode(TaxCat cat, decimal rate) => cat switch
    {
        TaxCat.Standard => FormatVatRate(rate),
        TaxCat.ZeroDomestic => "0 KR",
        TaxCat.ZeroIntraEu => "0 WDT",
        TaxCat.ZeroExport => "0 EX",
        TaxCat.Exempt => "zw",
        TaxCat.ReverseChargeEu => "oo",
        _ => FormatVatRate(rate)
    };

    private XElement BuildPodmiot2(BuyerData buyer)
    {
        var countryCode = string.IsNullOrWhiteSpace(buyer.CountryCode) ? "PL" : buyer.CountryCode.Trim().ToUpperInvariant();
        var isPolish = countryCode == "PL";
        var isEu = !isPolish && EuCountryCodes.Contains(IsoCountry(countryCode));
        var vatNumber = buyer.NIP?.Trim() ?? "";

        var identyfikacyjne = new XElement(Ns + "DaneIdentyfikacyjne");

        if (isPolish && !string.IsNullOrWhiteSpace(vatNumber))
        {
            identyfikacyjne.Add(new XElement(Ns + "NIP", CleanNip(vatNumber)));
        }
        else if (isEu && !string.IsNullOrWhiteSpace(vatNumber))
        {
            // Strip country prefix if present (e.g., "DK12345678" -> "12345678")
            var cleanVat = vatNumber;
            if (cleanVat.Length > 2 && char.IsLetter(cleanVat[0]) && char.IsLetter(cleanVat[1]))
                cleanVat = cleanVat[2..];
            identyfikacyjne.Add(new XElement(Ns + "KodUE", countryCode));
            identyfikacyjne.Add(new XElement(Ns + "NrVatUE", cleanVat));
        }
        else
        {
            identyfikacyjne.Add(new XElement(Ns + "BrakID", 1));
        }

        identyfikacyjne.Add(new XElement(Ns + "Nazwa", buyer.Name));

        return new XElement(Ns + "Podmiot2",
            identyfikacyjne,
            new XElement(Ns + "Adres",
                new XElement(Ns + "KodKraju", IsoCountry(countryCode)),
                new XElement(Ns + "AdresL1", FormatAddress(buyer.Street, buyer.BuildingNumber, buyer.ApartmentNumber)),
                new XElement(Ns + "AdresL2", $"{buyer.PostalCode} {buyer.City}")
            ),
            // FA(3): JST and GV are mandatory in Podmiot2 and must follow Adres.
            // "2" = not applicable (buyer is neither a local-government unit (JST)
            // nor a VAT-group member (GV)). If you ever invoice JST or VAT-group
            // buyers, these must be driven from buyer data instead of hardcoded.
            new XElement(Ns + "JST", 2),
            new XElement(Ns + "GV", 2)
        );
    }

    private XElement BuildFa(InvoiceData inv)
    {
        var currencyCode = string.IsNullOrWhiteSpace(inv.CurrencyCode) ? "PLN" : inv.CurrencyCode;
        var isCreditMemo = inv.IsCreditMemo;
        var zeroKind = ClassifyZeroRate(inv.Buyer);

        // A credit note (KOR) is a value correction: its totals carry the *difference*
        // vs the original (negative for a return/partial credit). Regular invoices are
        // positive. BC stores both as positive line amounts, so apply the sign here.
        var sign = isCreditMemo ? -1m : 1m;
        var totalGross = inv.Lines.Sum(l => Math.Abs(l.GrossAmount)) * sign;

        // Resolve each line's FA(3) tax category once (BC-supplied, or inferred).
        var resolved = inv.Lines.Select(l => (line: l, cat: ResolveCategory(l, zeroKind))).ToList();

        var fa = new XElement(Ns + "Fa",
            new XElement(Ns + "KodWaluty", currencyCode),
            new XElement(Ns + "P_1", inv.IssueDate.ToString("yyyy-MM-dd")),
            new XElement(Ns + "P_2", inv.InvoiceNumber),
            new XElement(Ns + "P_6", inv.SaleDate?.ToString("yyyy-MM-dd") ?? inv.IssueDate.ToString("yyyy-MM-dd"))
        );

        // Net/VAT sales totals per FA(3) category, emitted in schema order. Same shape for
        // invoices and corrections (correction amounts are negative differences):
        //   standard: 23% -> P_13_1/P_14_1, 8% -> P_13_2/P_14_2, 5% -> P_13_3/P_14_3
        //   0% domestic -> P_13_6_1, intra-EU WDT -> P_13_6_2, export -> P_13_6_3
        //   exempt (ZW) -> P_13_7, intra-EU reverse-charge services -> P_13_9
        // Not modelled: P_13_8 (other services outside PL) and P_13_10 (domestic reverse-charge).
        void AddStdPair(string netField, string vatField, decimal rate)
        {
            var sel = resolved.Where(r => r.cat == TaxCat.Standard && r.line.VatRate == rate).ToList();
            var net = sel.Sum(r => Math.Abs(r.line.NetAmount)) * sign;
            var vat = sel.Sum(r => Math.Abs(r.line.VatAmount)) * sign;
            if (net == 0m && vat == 0m) return;
            fa.Add(new XElement(Ns + netField, net.ToString("F2", CultureInfo.InvariantCulture)));
            fa.Add(new XElement(Ns + vatField, vat.ToString("F2", CultureInfo.InvariantCulture)));
        }
        void AddNet(string netField, TaxCat cat)
        {
            var net = resolved.Where(r => r.cat == cat).Sum(r => Math.Abs(r.line.NetAmount)) * sign;
            if (net != 0m)
                fa.Add(new XElement(Ns + netField, net.ToString("F2", CultureInfo.InvariantCulture)));
        }

        AddStdPair("P_13_1", "P_14_1", 23m);
        AddStdPair("P_13_2", "P_14_2", 8m);
        AddStdPair("P_13_3", "P_14_3", 5m);
        AddNet("P_13_6_1", TaxCat.ZeroDomestic);
        AddNet("P_13_6_2", TaxCat.ZeroIntraEu);
        AddNet("P_13_6_3", TaxCat.ZeroExport);
        AddNet("P_13_7", TaxCat.Exempt);
        AddNet("P_13_9", TaxCat.ReverseChargeEu);

        fa.Add(new XElement(Ns + "P_15", totalGross.ToString("F2", CultureInfo.InvariantCulture)));

        // Adnotacje markers: P_18 = "odwrotne obciążenie" (reverse-charge present);
        // Zwolnienie = exemption flag + statutory basis (P_19A) when any line is exempt.
        var hasReverseCharge = resolved.Any(r => r.cat == TaxCat.ReverseChargeEu);
        var hasExempt = resolved.Any(r => r.cat == TaxCat.Exempt);

        XElement zwolnienie;
        if (hasExempt)
        {
            var basis = inv.ExemptionLegalBasis?.Trim();
            if (string.IsNullOrEmpty(basis))
                throw new InvalidOperationException(
                    "Invoice has an exempt (ZW) line but ExemptionLegalBasis (FA(3) P_19A) is not set.");
            zwolnienie = new XElement(Ns + "Zwolnienie",
                new XElement(Ns + "P_19", 1),
                new XElement(Ns + "P_19A", basis));
        }
        else
        {
            zwolnienie = new XElement(Ns + "Zwolnienie", new XElement(Ns + "P_19N", 1));
        }

        fa.Add(new XElement(Ns + "Adnotacje",
            new XElement(Ns + "P_16", 2),
            new XElement(Ns + "P_17", 2),
            new XElement(Ns + "P_18", hasReverseCharge ? 1 : 2),
            new XElement(Ns + "P_18A", 2),
            zwolnienie,
            new XElement(Ns + "NoweSrodkiTransportu",
                new XElement(Ns + "P_22N", 1)
            ),
            new XElement(Ns + "P_23", 2),
            new XElement(Ns + "PMarzy",
                new XElement(Ns + "P_PMarzyN", 1)
            )
        ));

        fa.Add(new XElement(Ns + "RodzajFaktury", isCreditMemo ? "KOR" : "VAT"));

        if (isCreditMemo)
        {
            // Reason for correction
            fa.Add(new XElement(Ns + "PrzyczynaKorekty",
                !string.IsNullOrWhiteSpace(inv.CorrectionReason) ? inv.CorrectionReason : "Korekta faktury"));

            // TypKorekty: 1=korekta wartościowa, 2=korekta ilościowa, 3=korekta danych
            fa.Add(new XElement(Ns + "TypKorekty", 1));

            // DaneFaKorygowanej — reference to the corrected invoice. Exact sequence per FA(3) schemat.xsd:
            //   1) DataWystFaKorygowanej (required)
            //   2) NrFaKorygowanej (required)
            //   3) CHOICE: (NrKSeF + NrKSeFFaKorygowanej) when the corrected invoice is in KSeF, else NrKSeFN
            var daneFaKorygowanej = new XElement(Ns + "DaneFaKorygowanej",
                new XElement(Ns + "DataWystFaKorygowanej",
                    inv.OriginalInvoiceDate?.ToString("yyyy-MM-dd") ?? inv.IssueDate.ToString("yyyy-MM-dd")),
                new XElement(Ns + "NrFaKorygowanej",
                    string.IsNullOrWhiteSpace(inv.OriginalInvoiceNumber) ? inv.InvoiceNumber : inv.OriginalInvoiceNumber));
            if (!string.IsNullOrWhiteSpace(inv.OriginalInvoiceKSeFNumber))
            {
                daneFaKorygowanej.Add(new XElement(Ns + "NrKSeF", 1));
                daneFaKorygowanej.Add(new XElement(Ns + "NrKSeFFaKorygowanej", inv.OriginalInvoiceKSeFNumber));
            }
            else
                daneFaKorygowanej.Add(new XElement(Ns + "NrKSeFN", 1));
            fa.Add(daneFaKorygowanej);
            // P_15ZK is only for advance-invoice (zaliczkowe) corrections, which this
            // system does not issue, so it is intentionally omitted for value corrections.
        }

        // Invoice/correction lines. For a correction the quantity and net amount are
        // negative, mirroring the summary difference (supports partial credits).
        foreach (var (line, cat) in resolved)
        {
            var vatCode = LineVatCode(cat, line.VatRate);
            var lineElement = new XElement(Ns + "FaWiersz",
                new XElement(Ns + "NrWierszaFa", line.LineNumber),
                new XElement(Ns + "P_7", line.Description),
                new XElement(Ns + "P_8A", line.UnitOfMeasure),
                new XElement(Ns + "P_8B", (Math.Abs(line.Quantity) * sign).ToString("F4", CultureInfo.InvariantCulture)),
                new XElement(Ns + "P_9A", Math.Abs(line.UnitPrice).ToString("F2", CultureInfo.InvariantCulture)),
                new XElement(Ns + "P_11", (Math.Abs(line.NetAmount) * sign).ToString("F2", CultureInfo.InvariantCulture)),
                new XElement(Ns + "P_12", vatCode)
            );

            fa.Add(lineElement);
        }

        // Payment info
        if (inv.PaymentDueDate.HasValue)
        {
            fa.Add(new XElement(Ns + "Platnosc",
                new XElement(Ns + "TerminPlatnosci",
                    new XElement(Ns + "Termin", inv.PaymentDueDate.Value.ToString("yyyy-MM-dd"))
                ),
                new XElement(Ns + "FormaPlatnosci", MapPaymentMethod(inv.PaymentMethod)),
                !string.IsNullOrEmpty(inv.BankAccountNumber)
                    ? new XElement(Ns + "RachunekBankowy",
                        new XElement(Ns + "NrRB", inv.BankAccountNumber))
                    : null!
            ));
        }

        return fa;
    }

    private static string CleanNip(string nip)
    {
        var digits = new string(nip.Where(char.IsDigit).ToArray());
        // Polish NIP must be exactly 10 digits
        if (digits.Length > 10)
            digits = digits[..10];
        return digits;
    }

    private static string FormatAddress(string street, string building, string? apartment) =>
        string.IsNullOrEmpty(apartment)
            ? $"{street} {building}"
            : $"{street} {building}/{apartment}";

    private static string FormatVatRate(decimal rate) => rate switch
    {
        23 => "23",
        8 => "8",
        5 => "5",
        // FA(3) replaced bare "0" with classified zero-rate codes: "0 KR" (domestic),
        // "0 WDT" (intra-EU), "0 EX" (export). Defaulting to domestic to match the
        // P_13_6_1 summary bucket; EU/export sales need explicit classification.
        0 => "0 KR",
        _ => rate.ToString("F0", CultureInfo.InvariantCulture)
    };

    private static string MapPaymentMethod(string method) => method.ToLower() switch
    {
        "transfer" or "bank transfer" => "6",
        "cash" => "1",
        "card" or "credit card" => "2",
        "cheque" or "check" => "3",
        _ => "6" // default to transfer
    };
}
