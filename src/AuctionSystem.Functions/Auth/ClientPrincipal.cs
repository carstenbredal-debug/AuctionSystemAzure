using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuctionSystem.Functions.Auth;

/// <summary>
/// The Static Web Apps / App Service Easy Auth client principal, decoded from the
/// x-ms-client-principal header that the platform injects server-side (and strips if a
/// client tries to spoof it). Trustworthy only when the API is reached through the SWA
/// linked backend or App Service authentication — never when the Function App is exposed
/// directly to the internet.
/// </summary>
public sealed class ClientPrincipal
{
    [JsonPropertyName("identityProvider")] public string? IdentityProvider { get; set; }
    [JsonPropertyName("userId")] public string? UserId { get; set; }
    [JsonPropertyName("userDetails")] public string? UserDetails { get; set; }
    [JsonPropertyName("userRoles")] public string[]? UserRoles { get; set; }
    [JsonPropertyName("claims")] public PrincipalClaim[]? Claims { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static ClientPrincipal? FromHeader(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(headerValue));
            return JsonSerializer.Deserialize<ClientPrincipal>(json, Opts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The AAD / External-ID object id, matched against AppUser.AzureAdObjectId.</summary>
    public string? ObjectId =>
        Claims?.FirstOrDefault(c =>
            c.Type is "http://schemas.microsoft.com/identity/claims/objectidentifier" or "oid")?.Value
        ?? UserId;

    public string? Email =>
        Claims?.FirstOrDefault(c =>
            c.Type is "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"
                or "emails" or "email")?.Value
        ?? UserDetails;
}

public sealed class PrincipalClaim
{
    [JsonPropertyName("typ")] public string? Type { get; set; }
    [JsonPropertyName("val")] public string? Value { get; set; }
}
