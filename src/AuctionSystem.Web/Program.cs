using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using AuctionSystem.Web;
using AuctionSystem.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var configuredApiBase = builder.Configuration["ApiBaseUrl"];
var apiBase = string.IsNullOrWhiteSpace(configuredApiBase) ? builder.HostEnvironment.BaseAddress : configuredApiBase;
var swaBase = builder.HostEnvironment.BaseAddress;

builder.Services.AddHttpClient("SwaAuth", client => client.BaseAddress = new Uri(swaBase));
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(apiBase) });
builder.Services.AddScoped<AuctionApiClient>();
builder.Services.AddScoped<SwaAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<SwaAuthStateProvider>());
builder.Services.AddAuthorizationCore();

await builder.Build().RunAsync();
