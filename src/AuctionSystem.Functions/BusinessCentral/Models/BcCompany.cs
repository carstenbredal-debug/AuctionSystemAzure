using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.BusinessCentral.Models;

public class BcCompany
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("systemVersion")]
    public string SystemVersion { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("businessProfileId")]
    public string BusinessProfileId { get; set; } = string.Empty;
}
