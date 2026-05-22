using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        var connectionString = context.Configuration["SqlConnectionString"]
            ?? context.Configuration["Values:SqlConnectionString"]
            ?? throw new InvalidOperationException("SqlConnectionString is not configured");

        services.AddDbContext<AuctionDbContext>(options =>
            options.UseSqlServer(connectionString));

        services.AddScoped<AuctionService>();
        services.AddScoped<BidService>();
        services.AddScoped<SettlementService>();
    })
    .Build();

host.Run();
