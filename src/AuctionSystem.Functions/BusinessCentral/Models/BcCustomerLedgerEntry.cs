using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.BusinessCentral.Models;

public class BcCustomerLedgerEntry
{
    [JsonPropertyName("entryNo")]
    public int EntryNo { get; set; }

    [JsonPropertyName("postingDate")]
    public string PostingDate { get; set; } = string.Empty;

    [JsonPropertyName("documentType")]
    public string DocumentType { get; set; } = string.Empty;

    [JsonPropertyName("documentNo")]
    public string DocumentNo { get; set; } = string.Empty;

    [JsonPropertyName("customerNo")]
    public string CustomerNo { get; set; } = string.Empty;

    [JsonPropertyName("customerName")]
    public string CustomerName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("currencyCode")]
    public string CurrencyCode { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("remainingAmount")]
    public decimal RemainingAmount { get; set; }

    [JsonPropertyName("originalAmount")]
    public decimal OriginalAmount { get; set; }

    [JsonPropertyName("open")]
    public bool Open { get; set; }

    [JsonPropertyName("dueDate")]
    public string DueDate { get; set; } = string.Empty;

    [JsonPropertyName("closedByEntryNo")]
    public int ClosedByEntryNo { get; set; }

    [JsonPropertyName("closedAtDate")]
    public string ClosedAtDate { get; set; } = string.Empty;

    [JsonPropertyName("externalDocumentNo")]
    public string ExternalDocumentNo { get; set; } = string.Empty;
}
