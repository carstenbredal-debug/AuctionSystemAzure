namespace KSeF.Functions.Models;

public class JpkV7MRequest
{
    public CompanyData Company { get; set; } = new();
    public int Year { get; set; }
    public int Month { get; set; }
    public string Purpose { get; set; } = "Original";
    public int CorrectionNo { get; set; }
    public List<SalesRecordData> SalesRecords { get; set; } = new();
}

public class CompanyData
{
    public string Nip { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
}

public class SalesRecordData
{
    public string DocumentType { get; set; } = "FP";
    public string InvoiceNumber { get; set; } = "";
    public string IssueDate { get; set; } = "";
    public string? SaleDate { get; set; }
    public string CounterpartyNip { get; set; } = "";
    public string CounterpartyName { get; set; } = "";
    public string? CountryCode { get; set; }
    public string? PaymentDueDate { get; set; }
    public string? KSeFReferenceNumber { get; set; }
    public bool IsCreditMemo { get; set; }

    // Per-category VAT breakdown of the document, supplied by BC (grouped by VAT Bus.
    // Posting Group / VAT clause). Drives the JPK K_/P_ boxes. When empty, the builder
    // falls back to a single component derived from NetAmount/VatAmount (back-compat).
    public List<SalesVatBreakdown> Vat { get; set; } = new();

    public decimal NetAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal GrossAmount { get; set; }
}

public class SalesVatBreakdown
{
    // STD = domestic rated (POLAND), ZW = exempt, OO = intra-EU reverse-charge (EU),
    // NP = outside-scope / not-subject (NONEU).
    public string Category { get; set; } = "STD";
    public decimal NetAmount { get; set; }
    public decimal VatAmount { get; set; }
}

public class JpkV7MResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Xml { get; set; }
    public string? FileName { get; set; }
    public int SalesRecordCount { get; set; }
    public int PurchaseRecordCount { get; set; }
    public decimal TaxDue { get; set; }
    public decimal TaxDeductible { get; set; }
    public bool SchemaValid { get; set; }
    public List<string> ValidationErrors { get; set; } = new();
}
