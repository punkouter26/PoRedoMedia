using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Api.Features.Runs;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Configuration;

public static class ConfigEndpoint
{
    /// <summary>Anonymous: the login page needs it before anyone is signed in. Nothing here is secret.</summary>
    public static IEndpointRouteBuilder MapAppConfig(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config", (IConfiguration configuration, IWebHostEnvironment env, RunExecutor executor, IServiceProvider services) =>
            TypedResults.Ok(new AppConfigDto(
                MockAi.IsEnabled(configuration, env), env.IsDevOrTest(), [.. executor.Available.Order()],
                [.. SessionRoast.Voices(configuration, env)],
                [.. (services.GetService<IVisionServiceRouter>() as VisionServiceRouter)?.Available ?? []],
                new AiPricingDto(
                    Price("VisionUsd", 0.001m), Price("TextUsd", 0.0015m), Price("ImageUsd", 0.039m), Price("MusicUsd", 0.04m), Price("VideoUsd", 0.40m)))))
            .AllowAnonymous();
        return app;

        decimal Price(string name, decimal fallback) => app.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<decimal?>($"AiPricing:{name}") ?? fallback;
    }
}
