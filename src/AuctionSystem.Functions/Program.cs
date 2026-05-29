using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Services;
using AuctionSystem.Functions.BusinessCentral.Configuration;
using AuctionSystem.Functions.BusinessCentral.Services;
using AuctionSystem.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        var connectionString = context.Configuration["SqlConnectionString"]
            ?? context.Configuration["Values:SqlConnectionString"]
            ?? throw new InvalidOperationException("SqlConnectionString is not configured");

        services.AddDbContext<AuctionDbContext>(options =>
            options.UseSqlServer(connectionString));

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseSqlServer(connectionString));

        services.AddScoped<AuctionService>();
        services.AddScoped<BidService>();
        services.AddScoped<SettlementService>();

        // Business Central integration
        services.Configure<BusinessCentralOptions>(opts =>
        {
            opts.TenantId = context.Configuration["BC_TENANT_ID"]
                ?? context.Configuration["Values:BC_TENANT_ID"] ?? "";
            opts.ClientId = context.Configuration["BC_CLIENT_ID"]
                ?? context.Configuration["Values:BC_CLIENT_ID"] ?? "";
            opts.ClientSecret = context.Configuration["BC_CLIENT_SECRET"]
                ?? context.Configuration["Values:BC_CLIENT_SECRET"] ?? "";
            opts.Environment = context.Configuration["BC_ENVIRONMENT"]
                ?? context.Configuration["Values:BC_ENVIRONMENT"] ?? "sandbox";
            opts.CompanyId = context.Configuration["BC_COMPANY_ID"]
                ?? context.Configuration["Values:BC_COMPANY_ID"] ?? "";
        });

        var bcTenantId = context.Configuration["BC_TENANT_ID"]
            ?? context.Configuration["Values:BC_TENANT_ID"] ?? "";
        if (!string.IsNullOrEmpty(bcTenantId))
        {
            services.AddSingleton<BusinessCentralAuthService>();
            services.AddHttpClient<BusinessCentralApiClient>();
            services.AddScoped<BusinessCentralSyncService>();
        }

        var storageConnectionString = context.Configuration["AzureWebJobsStorage"]
            ?? context.Configuration["Values:AzureWebJobsStorage"];
        if (!string.IsNullOrEmpty(storageConnectionString) && storageConnectionString != "UseDevelopmentStorage=true")
        {
            services.AddSingleton(sp => new BlobStorageService(
                storageConnectionString,
                sp.GetRequiredService<ILogger<BlobStorageService>>()));
        }
    })
    .Build();

// Apply pending EF Core migrations on startup
using (var scope = host.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();
        // Add columns not covered by EF migrations (no Designer file)
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Brokers') AND name = 'CreditLimit')
                ALTER TABLE auction.Brokers ADD CreditLimit decimal(18,2) NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Brokers') AND name = 'Blocked')
                ALTER TABLE auction.Brokers ADD Blocked nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Brokers') AND name = 'GenBusPostingGroup')
                ALTER TABLE auction.Brokers ADD GenBusPostingGroup nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Brokers') AND name = 'VatBusPostingGroup')
                ALTER TABLE auction.Brokers ADD VatBusPostingGroup nvarchar(max) NOT NULL DEFAULT '';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('auction.Brokers') AND name = 'CustomerPostingGroup')
                ALTER TABLE auction.Brokers ADD CustomerPostingGroup nvarchar(max) NOT NULL DEFAULT '';
        ");
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to apply database migrations on startup");
    }
}

host.Run();
