namespace AuctionSystem.Functions.BusinessCentral.Configuration;

public class BusinessCentralOptions
{
    public const string SectionName = "BusinessCentral";

    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Environment { get; set; } = "sandbox";
    public string CompanyId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;

    public string BaseUrl =>
        $"https://api.businesscentral.dynamics.com/v2.0/{TenantId}/{Environment}/api/v2.0";

    public string CustomApiBaseUrl =>
        $"https://api.businesscentral.dynamics.com/v2.0/{TenantId}/{Environment}/api/auctionSystem/integration/v1.0";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(TenantId) &&
        !string.IsNullOrEmpty(ClientId) &&
        !string.IsNullOrEmpty(ClientSecret);
}
