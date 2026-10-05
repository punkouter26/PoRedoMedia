using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Common;

public static class ConfigEndpoint
{
    /// <summary>Anonymous: the login page needs it before anyone is signed in. Nothing here is secret.</summary>
    public static IEndpointRouteBuilder MapAppConfig(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config", (IConfiguration configuration, IWebHostEnvironment env) =>
            TypedResults.Ok(new AppConfigDto(MockAi.IsEnabled(configuration, env), env.IsDevOrTest()))).AllowAnonymous();
        return app;
    }
}
