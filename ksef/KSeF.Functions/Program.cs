using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KSeF.Functions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddHttpClient<KSeFApiClient>();
        services.AddSingleton<InvoiceXmlBuilder>();
        services.AddSingleton<FaValidator>();
        services.AddSingleton<JpkV7MBuilder>();
        services.AddSingleton<JpkValidator>();
    })
    .Build();

host.Run();
