using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.BusinessCentral.Models;

public class BcCurrency
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;
}
