using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PoRedoMedia.Client.Http;
using PoRedoMedia.Client.LocalAi;
using PoRedoMedia.Client.Services;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);

// Errors on the user's screen reach the server's logs too.
var errors = new ClientErrorReporter(baseAddress);
builder.Logging.AddProvider(errors);
builder.Services.AddScoped(services => new HttpClient(
    new SessionExpiredHandler(services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>())
    {
        InnerHandler = new AntiforgeryTokenHandler(() => new HttpClient { BaseAddress = baseAddress }) { InnerHandler = new HttpClientHandler() },
    })
{
    BaseAddress = baseAddress,
    // Long enough for a chained image run; video runs report progress over SignalR instead.
    Timeout = TimeSpan.FromMinutes(4),
});

builder.Services.AddScoped<AppConfigService>();
builder.Services.AddScoped<MediaApi>();
builder.Services.AddScoped<BlobUploadService>();
builder.Services.AddScoped<RunApi>();
builder.Services.AddScoped<SessionCostService>();
builder.Services.AddScoped<RunTracker>();
builder.Services.AddScoped<UiStore>();
builder.Services.AddScoped<AppSettings>();
builder.Services.AddScoped<LocalAiService>();
builder.Services.AddRadzenComponents();

// Claims only, serialized by the server. The browser never holds a token.
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();

var host = builder.Build();
errors.Page = () => host.Services.GetService<Microsoft.AspNetCore.Components.NavigationManager>()?.Uri;
await host.RunAsync();
