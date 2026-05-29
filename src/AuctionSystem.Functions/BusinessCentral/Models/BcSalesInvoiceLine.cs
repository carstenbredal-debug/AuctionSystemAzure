using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.BusinessCentral.Models;

public class BcSalesInvoiceLine
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid Id { get; set; }

    [JsonPropertyName("documentId")]
    public Guid DocumentId { get; set; }

    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }

    [JsonPropertyName("lineType")]
    public string LineType { get; set; } = "Item";

    [JsonPropertyName("lineObjectNumber")]
    public string LineObjectNumber { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("lineAmount")]
    public decimal LineAmount { get; set; }

    [JsonPropertyName("@odata.etag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ETag { get; set; }
}
