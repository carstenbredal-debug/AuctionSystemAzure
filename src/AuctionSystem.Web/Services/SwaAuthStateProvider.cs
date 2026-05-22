using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace AuctionSystem.Web.Services;

public class SwaAuthStateProvider : AuthenticationStateProvider
{
    private readonly HttpClient _http;
    private readonly AuctionApiClient _apiClient;

    public SwaAuthStateProvider(HttpClient http, AuctionApiClient apiClient)
    {
        _http = http;
        _apiClient = apiClient;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var authData = await _http.GetFromJsonAsync<SwaAuthData>("/.auth/me");
            var principal = authData?.ClientPrincipal;

            if (principal == null || string.IsNullOrEmpty(principal.UserId))
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, principal.UserId),
                new(ClaimTypes.Name, principal.UserDetails ?? principal.UserId),
                new("idp", principal.IdentityProvider ?? "aad")
            };

            // Get the user's role from our backend
            var userInfo = await _apiClient.GetCurrentUserAsync(principal.UserId);
            if (userInfo != null)
            {
                claims.Add(new Claim(ClaimTypes.Role, userInfo.Role));
                claims.Add(new Claim(ClaimTypes.Email, userInfo.Email));
            }

            foreach (var role in principal.UserRoles ?? [])
            {
                if (role != "anonymous" && role != "authenticated")
                    claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var identity = new ClaimsIdentity(claims, "swa");
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }
    }

    public void NotifyAuthStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}

public class SwaAuthData
{
    public SwaClientPrincipal? ClientPrincipal { get; set; }
}

public class SwaClientPrincipal
{
    public string? IdentityProvider { get; set; }
    public string? UserId { get; set; }
    public string? UserDetails { get; set; }
    public string[]? UserRoles { get; set; }
    public SwaAuthClaim[]? Claims { get; set; }
}

public class SwaAuthClaim
{
    public string? Typ { get; set; }
    public string? Val { get; set; }
}
