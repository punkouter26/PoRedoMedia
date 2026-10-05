using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PoRedoMedia.Client.Http;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
builder.Services.AddScoped(_ => new HttpClient(
    new CorrelationHeaderHandler
    {
        InnerHandler = new AntiforgeryTokenHandler(() => new HttpClient { BaseAddress = baseAddress })
        {
            InnerHandler = new HttpClientHandler(),
        },
    })
{
    BaseAddress = baseAddress,
    // Long enough for a chained image run; video runs report progress over SignalR instead.
    Timeout = TimeSpan.FromMinutes(4),
});

await builder.Build().RunAsync();
