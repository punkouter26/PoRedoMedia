using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using PoRedoMedia.Api.Components;
using PoRedoMedia.Api.Configuration;
using PoRedoMedia.Api.Features.Auth;
using PoRedoMedia.Api.Features.BulkStyles;
using PoRedoMedia.Api.Features.Captions;
using PoRedoMedia.Api.Features.Housekeeping;
using PoRedoMedia.Api.Features.Media;
using PoRedoMedia.Api.Features.MemeCaption;
using PoRedoMedia.Api.Features.Quota;
using PoRedoMedia.Api.Features.Runs;
using PoRedoMedia.Api.Features.Sharing;
using PoRedoMedia.Api.Features.Sounds;
using PoRedoMedia.Api.Hubs;
using Radzen;

// CLI verb: dotnet run --project src/PoRedoMedia.Api -- seed-sounds [--seeds-dir <path>]
// Fills the sound library and exits, without starting the web host.
if (args.Length > 0 && args[0] == "seed-sounds")
{
    var seedConfiguration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("appsettings.Development.json", optional: true)
        .AddEnvironmentVariables()
        .Build();
    return await SeedSoundsCommand.RunAsync(args[1..], seedConfiguration);
}

var builder = WebApplication.CreateBuilder(args);

// The static web assets manifest is wired automatically only in Development. Test runs from
// build output too, and without this every asset answers empty and the WASM app never boots.
if (builder.Environment.IsEnvironment(PoEnvironments.Test))
    builder.WebHost.UseStaticWebAssets();

builder.AddPoRedoMediaKeyVault();

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization();
builder.Services.AddRadzenComponents();
builder.Services.AddSingleton<StorageClients>();
builder.Services.AddSingleton<BlobStorageService>();
builder.Services.AddSingleton<FFmpegProcess>();
builder.Services.AddSingleton<IMediaRepository, MediaTableRepository>();
builder.Services.AddSingleton<Thumbnails>();
builder.Services.AddSingleton<MediaOutputs>();
builder.Services.AddSingleton<ShareLinkStore>();
builder.Services.AddSingleton<IShareLinks>(services => services.GetRequiredService<ShareLinkStore>());
builder.Services.AddFunctions(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRenderQuota, RenderQuotaService>();
builder.Services.AddSingleton<IRunRepository, RunTableRepository>();
builder.Services.AddSingleton<IRunNotifier, RunHubNotifier>();
builder.Services.AddSingleton<RunExecutor>();
builder.Services.AddSingleton<RunDispatcher>();
builder.Services.AddHostedService(services => services.GetRequiredService<RunDispatcher>());
builder.Services.AddHostedService<HousekeepingService>();
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Wire enums travel as their names, matching the client's source-generated JSON.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddPoAntiforgery(builder.Environment);
builder.Services.AddPoRedoMediaAuth(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseAuthentication();

// The WASM boot assets under /_framework must load for signed-out users, or the login page cannot
// start. Most are static files, but the runtime-generated boot manifest is not, so
// MapStaticAssets().AllowAnonymous() misses it and the fallback policy would redirect it to /login.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/_framework")
        && context.GetEndpoint() is { } endpoint
        && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
    {
        context.SetEndpoint(new Endpoint(
            endpoint.RequestDelegate,
            new EndpointMetadataCollection([.. endpoint.Metadata, new AllowAnonymousAttribute()]),
            endpoint.DisplayName));
    }

    await next();
});

app.UseAuthorization();
app.UseAntiforgery();

app.MapHealth();
app.MapAntiforgeryToken();
app.MapAppConfig();
app.MapAuthEndpoints();
app.MapMedia();
app.MapQuota();
app.MapRuns();
app.MapMemeTemplates();
app.MapBulkPrompts();
app.MapSounds();
app.MapCaptions();
app.MapSharing();
app.MapHub<RunHub>(RunHub.Path);
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(PoRedoMedia.Client.Routes).Assembly)
    .AllowAnonymous();

// The browser uploads to and reads from blob storage directly, so storage must accept the app's
// origin. Any origin is allowed: every such request already needs a signed, expiring link.
if (app.Services.GetRequiredService<StorageClients>() is { IsConfigured: true } storage)
{
    try
    {
        await storage.AllowBrowserAccessAsync("*");
    }
    catch (Exception ex) when (ex is Azure.RequestFailedException or AggregateException)
    {
        app.Logger.LogWarning(ex, "Storage is not reachable; uploads will fail until it is and the app is restarted");
    }
}

app.Run();
return 0;

public partial class Program;
