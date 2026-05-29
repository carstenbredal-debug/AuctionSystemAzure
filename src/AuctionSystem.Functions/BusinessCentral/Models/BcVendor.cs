using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.BusinessCentral.Models;

public class BcVendor
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid Id { get; set; }

    [JsonPropertyName("number")]
    public string Number { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("addressLine1")]
    public string AddressLine1 { get; set; } = string.Empty;

    [JsonPropertyName("addressLine2")]
    public string AddressLine2 { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("postalCode")]
    public string PostalCode { get; set; } = string.Empty;

    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("website")]
    public string Website { get; set; } = string.Empty;

    [JsonPropertyName("currencyCode")]
    public string CurrencyCode { get; set; } = string.Empty;

    [JsonPropertyName("blocked")]
    public string Blocked { get; set; } = string.Empty;

    [JsonPropertyName("genBusPostingGroup")]
    public string GenBusPostingGroup { get; set; } = string.Empty;

    [JsonPropertyName("vatBusPostingGroup")]
    public string VatBusPostingGroup { get; set; } = string.Empty;

    [JsonPropertyName("vendorPostingGroup")]
    public string VendorPostingGroup { get; set; } = string.Empty;

    [JsonPropertyName("paymentTermsCode")]
    public string PaymentTermsCode { get; set; } = string.Empty;

    [JsonPropertyName("paymentMethodCode")]
    public string PaymentMethodCode { get; set; } = string.Empty;

    [JsonPropertyName("taxLiable")]
    public bool TaxLiable { get; set; }

    [JsonPropertyName("lastModifiedDateTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastModifiedDateTime { get; set; }

    [JsonPropertyName("@odata.etag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ETag { get; set; }
}
