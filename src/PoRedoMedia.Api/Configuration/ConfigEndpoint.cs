using PoRedoMedia.Api.Features.Runs;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Configuration;

public static class ConfigEndpoint
{
    /// <summary>Anonymous: the login page needs it before anyone is signed in. Nothing here is secret.</summary>
    public static IEndpointRouteBuilder MapAppConfig(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config", (IConfiguration configuration, IWebHostEnvironment env, RunExecutor executor) =>
            TypedResults.Ok(new AppConfigDto(
                MockAi.IsEnabled(configuration, env), env.IsDevOrTest(), [.. executor.Available.Order()],
                [.. SessionRoast.Voices(configuration, env)]))).AllowAnonymous();
        return app;
    }
}
