using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Services;
using AuctionSystem.Web.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? builder.Configuration["SqlConnectionString"]
    ?? throw new InvalidOperationException("Connection string not configured");

builder.Services.AddDbContext<AuctionDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddScoped<AuctionService>();
builder.Services.AddScoped<BidService>();
builder.Services.AddScoped<SettlementService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();
    db.Database.Migrate();
    SeedData.Initialize(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
