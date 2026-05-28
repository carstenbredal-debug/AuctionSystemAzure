using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Services;
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

        var storageConnectionString = context.Configuration["AzureWebJobsStorage"]
            ?? context.Configuration["Values:AzureWebJobsStorage"];
        if (!string.IsNullOrEmpty(storageConnectionString) && storageConnectionString != "UseDevelopmentStorage=true")
        {
            services.AddSingleton(new BlobStorageService(storageConnectionString));
        }
    })
    .Build();

// Apply pending EF Core migrations on startup
using (var scope = host.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to apply database migrations on startup");
    }
}

host.Run();
