using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.BusinessCentral.Models;

public class BcCustomer
{
    [JsonPropertyName("systemId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid Id { get; set; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid StandardApiId { get => Guid.Empty; set { if (value != Guid.Empty) Id = value; } }

    [JsonPropertyName("number")]
    public string Number { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    [JsonIgnore]
    public string Type { get; set; } = "Company";

    [JsonPropertyName("addressLine1")]
    public string AddressLine1 { get; set; } = string.Empty;

    [JsonPropertyName("addressLine2")]
    public string AddressLine2 { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    [JsonIgnore]
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

    [JsonPropertyName("taxLiable")]
    [JsonIgnore]
    public bool TaxLiable { get; set; }

    [JsonPropertyName("currencyCode")]
    public string CurrencyCode { get; set; } = string.Empty;

    [JsonPropertyName("creditLimit")]
    public decimal CreditLimit { get; set; }

    [JsonPropertyName("blocked")]
    public string Blocked { get; set; } = string.Empty;

    [JsonPropertyName("genBusPostingGroup")]
    public string GenBusPostingGroup { get; set; } = string.Empty;

    [JsonPropertyName("vatBusPostingGroup")]
    public string VatBusPostingGroup { get; set; } = string.Empty;

    [JsonPropertyName("customerPostingGroup")]
    public string CustomerPostingGroup { get; set; } = string.Empty;

    [JsonPropertyName("paymentTermsCode")]
    public string PaymentTermsCode { get; set; } = string.Empty;

    [JsonPropertyName("paymentMethodCode")]
    public string PaymentMethodCode { get; set; } = string.Empty;

    [JsonPropertyName("vatRegistrationNo")]
    public string VatRegistrationNo { get; set; } = string.Empty;

    [JsonPropertyName("lastModifiedDateTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastModifiedDateTime { get; set; }

    [JsonPropertyName("@odata.etag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ETag { get; set; }
}
