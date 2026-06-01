using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.BusinessCentral.Models;

public class BcCustomerPayment
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("lineNumber")]
    public int LineNumber { get; set; }

    [JsonPropertyName("customerNumber")]
    public string CustomerNumber { get; set; } = string.Empty;

    [JsonPropertyName("customerName")]
    public string CustomerName { get; set; } = string.Empty;

    [JsonPropertyName("postingDate")]
    public string PostingDate { get; set; } = string.Empty;

    [JsonPropertyName("documentNumber")]
    public string DocumentNumber { get; set; } = string.Empty;

    [JsonPropertyName("externalDocumentNumber")]
    public string ExternalDocumentNumber { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("appliesToInvoiceNumber")]
    public string AppliesToInvoiceNumber { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    [JsonPropertyName("lastModifiedDateTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastModifiedDateTime { get; set; }
}
