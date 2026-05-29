using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.BusinessCentral.Models;

public class BcItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("number")]
    public string Number { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "Inventory";

    [JsonPropertyName("itemCategoryCode")]
    public string ItemCategoryCode { get; set; } = string.Empty;

    [JsonPropertyName("blocked")]
    public bool Blocked { get; set; }

    [JsonPropertyName("gtin")]
    public string Gtin { get; set; } = string.Empty;

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("unitCost")]
    public decimal UnitCost { get; set; }

    [JsonPropertyName("baseUnitOfMeasureCode")]
    public string BaseUnitOfMeasureCode { get; set; } = string.Empty;

    [JsonPropertyName("inventory")]
    public decimal Inventory { get; set; }

    [JsonPropertyName("lastModifiedDateTime")]
    public DateTimeOffset? LastModifiedDateTime { get; set; }

    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; set; }
}
