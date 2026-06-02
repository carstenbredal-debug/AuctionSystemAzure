using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.BusinessCentral.Models;

public class BcPaymentApplication
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid Id { get; set; }

    [JsonPropertyName("customerNo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomerNo { get; set; }

    [JsonPropertyName("paymentEntryNo")]
    public int PaymentEntryNo { get; set; }

    [JsonPropertyName("invoiceDocumentNo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InvoiceDocumentNo { get; set; }

    [JsonPropertyName("amountToApply")]
    public decimal AmountToApply { get; set; }

    [JsonPropertyName("sourceDocumentType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceDocumentType { get; set; }

    [JsonPropertyName("resultStatus")]
    public string ResultStatus { get; set; } = string.Empty;

    [JsonPropertyName("resultMessage")]
    public string ResultMessage { get; set; } = string.Empty;
}
