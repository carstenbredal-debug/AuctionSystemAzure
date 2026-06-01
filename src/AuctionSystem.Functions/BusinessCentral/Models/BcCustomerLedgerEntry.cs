using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.BusinessCentral.Models;

public class BcCustomerLedgerEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("entryNumber")]
    public int EntryNumber { get; set; }

    [JsonPropertyName("postingDate")]
    public string PostingDate { get; set; } = string.Empty;

    [JsonPropertyName("documentNumber")]
    public string DocumentNumber { get; set; } = string.Empty;

    [JsonPropertyName("documentType")]
    public string DocumentType { get; set; } = string.Empty;

    [JsonPropertyName("sourceNumber")]
    public string SourceNumber { get; set; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("debitAmount")]
    public decimal DebitAmount { get; set; }

    [JsonPropertyName("creditAmount")]
    public decimal CreditAmount { get; set; }

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("lastModifiedDateTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastModifiedDateTime { get; set; }
}
