using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.BusinessCentral.Models;

public class BcPostingGroup
{
    [JsonPropertyName("Code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;
}
