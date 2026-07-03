using AuctionSystem.Functions.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

namespace AuctionSystem.Functions.Functions;

// Verifies a section password for the Blazor section gates: the client sends the entered password in
// that section's header (x-section-password-{section}); 200 = correct, 403 = wrong/unset. The comparison
// mirrors the middleware exactly (fail-closed on an unset SECTION_PASSWORD_{SECTION}). Admin-only.
public class SectionPasswordFunctions
{
    private readonly IConfiguration _config;

    public SectionPasswordFunctions(IConfiguration config) => _config = config;

    [AuctionSystem.Functions.Auth.RequireRole("Admin")]
    [Function("VerifySectionPassword")]
    public Task<HttpResponseData> Verify(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "section-password/verify")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var section = query["section"];
        if (string.IsNullOrWhiteSpace(section))
            return Task.FromResult(req.CreateResponse(System.Net.HttpStatusCode.BadRequest));

        var expected = AuthenticationMiddleware.SectionPasswordFor(_config, section);
        var provided = req.Headers.TryGetValues(AuthenticationMiddleware.SectionHeaderFor(section), out var vals)
            ? vals.FirstOrDefault() : null;

        var ok = !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(provided)
                 && string.Equals(provided, expected, StringComparison.Ordinal);
        return Task.FromResult(req.CreateResponse(ok ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.Forbidden));
    }
}
