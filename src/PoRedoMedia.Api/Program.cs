using Microsoft.AspNetCore.Authorization;
using PoRedoMedia.Api.Components;
using PoRedoMedia.Api.Configuration;
using PoRedoMedia.Api.Features.Auth;
using Radzen;

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
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(PoRedoMedia.Client.Routes).Assembly)
    .AllowAnonymous();

app.Run();

public partial class Program;
