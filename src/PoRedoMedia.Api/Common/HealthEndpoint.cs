namespace PoRedoMedia.Api.Common;

public static class HealthEndpoint
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        // Liveness only: answers as soon as the process serves requests, with no dependency checks.
        app.MapGet("/health/live", () => Results.Text("Healthy")).AllowAnonymous();
        return app;
    }
}
