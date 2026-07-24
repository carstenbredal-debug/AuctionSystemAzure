namespace KSeF.Functions.Models;

/// <summary>
/// Invoice data received from Business Central for submission to KSeF.
/// </summary>
public class InvoiceData
{
    public string InvoiceNumber { get; set; } = "";
    public DateTime IssueDate { get; set; }
    public DateTime? SaleDate { get; set; }

    // Seller (our company)
    public SellerData Seller { get; set; } = new();

    // Buyer
    public BuyerData Buyer { get; set; } = new();

    // Invoice lines
    public List<InvoiceLineData> Lines { get; set; } = new();

    // Currency
    public string CurrencyCode { get; set; } = "PLN";

    // Payment
    public string PaymentMethod { get; set; } = "transfer";
    public DateTime? PaymentDueDate { get; set; }
    public string? BankAccountNumber { get; set; }

    // Credit memo fields
    public bool IsCreditMemo { get; set; }
    public string? OriginalInvoiceKSeFNumber { get; set; }
    public string? OriginalInvoiceNumber { get; set; }
    public DateTime? OriginalInvoiceDate { get; set; }
    public string? CorrectionReason { get; set; }

    // FA(3) statutory legal basis for VAT exemption (P_19A), required when any line
    // is exempt (TaxCategory = "ZW"). Supplied by BC per invoice.
    public string? ExemptionLegalBasis { get; set; }

    // Self-invoicing (samofakturowanie, art. 106d): WE issue on behalf of the supplier.
    // Seller = the supplier (e.g. farmer), Buyer = our company. Sets Adnotacje P_17 = 1 and
    // the KSeF session is opened in the SELLER's NIP context — which requires the supplier
    // to have granted us the self-invoicing permission in KSeF (and, for token auth, a
    // KSeF token issued in their context; see KSeFApiClient.TokenFor).
    public bool SelfInvoicing { get; set; }
}

public class SellerData
{
    public string NIP { get; set; } = "";
    public string Name { get; set; } = "";
    public string Street { get; set; } = "";
    public string BuildingNumber { get; set; } = "";
    public string? ApartmentNumber { get; set; }
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string CountryCode { get; set; } = "PL";
}

public class BuyerData
{
    public string NIP { get; set; } = "";
    public string Name { get; set; } = "";
    public string Street { get; set; } = "";
    public string BuildingNumber { get; set; } = "";
    public string? ApartmentNumber { get; set; }
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string CountryCode { get; set; } = "PL";
}

public class InvoiceLineData
{
    public int LineNumber { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public string UnitOfMeasure { get; set; } = "szt.";
    public decimal UnitPrice { get; set; }
    public decimal NetAmount { get; set; }
    public decimal VatRate { get; set; } = 23;
    public decimal VatAmount { get; set; }
    public decimal GrossAmount { get; set; }

    // FA(3) tax category, supplied by BC (the VAT% alone cannot distinguish 0% / WDT /
    // export / exempt / reverse-charge). Recognised values:
    //   "STD" standard rate (use VatRate)   "KR"  0% domestic
    //   "WDT" 0% intra-EU goods             "EXP" 0% export
    //   "ZW"  VAT-exempt                    "OO"  intra-EU reverse-charge services
    // When null/empty, the category is inferred from VatRate + buyer country (back-compat).
    public string? TaxCategory { get; set; }
}
