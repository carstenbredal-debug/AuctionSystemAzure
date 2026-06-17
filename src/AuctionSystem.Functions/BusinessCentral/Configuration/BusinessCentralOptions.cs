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

    // BC Dimension systemIds used to tag synced master records (VENDORTYPE=BROKER/FARMER on vendors,
    // CUSTOMERTYPE=BUYER on customers). These are unique per BC company, so they must be set per
    // environment/company. Defaults are the "Lot Test 3" company's ids (see Program.cs); read the
    // right values for a new company via GET /api/bc/dimensions.
    public Guid VendorTypeDimensionId { get; set; }
    public Guid VendorTypeBrokerValueId { get; set; }
    public Guid VendorTypeFarmerValueId { get; set; }
    public Guid CustomerTypeDimensionId { get; set; }
    public Guid CustomerTypeBuyerValueId { get; set; }

    public string BaseUrl =>
        $"https://api.businesscentral.dynamics.com/v2.0/{TenantId}/{Environment}/api/v2.0";

    public string CustomApiBaseUrl =>
        $"https://api.businesscentral.dynamics.com/v2.0/{TenantId}/{Environment}/api/auctionSystem/integration/v1.0";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(TenantId) &&
        !string.IsNullOrEmpty(ClientId) &&
        !string.IsNullOrEmpty(ClientSecret);
}
