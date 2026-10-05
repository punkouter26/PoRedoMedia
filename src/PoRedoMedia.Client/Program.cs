using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PoRedoMedia.Client.Http;
using PoRedoMedia.Client.Services;
using Radzen;

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

builder.Services.AddScoped<AppConfigService>();
builder.Services.AddScoped<MediaApi>();
builder.Services.AddScoped<BlobUploadService>();
builder.Services.AddRadzenComponents();

// Claims only, serialized by the server. The browser never holds a token.
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();

await builder.Build().RunAsync();
