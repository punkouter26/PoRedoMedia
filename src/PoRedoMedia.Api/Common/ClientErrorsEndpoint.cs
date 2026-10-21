using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// Where the browser reports its own errors, so a failure on someone's screen reaches the logs
/// (and Application Insights) instead of dying in their console.
/// </summary>
public static partial class ClientErrorsEndpoint
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Browser error on {Page} in {Category}: {Message}\n{Stack}")]
    private static partial void LogBrowserError(ILogger logger, string page, string category, string message, string stack);

    public static IEndpointRouteBuilder MapClientErrors(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/client-errors").RequireAntiforgeryValidation()
            .MapPost("/", (ClientErrorDto error, ILoggerFactory loggers) =>
            {
                LogBrowserError(
                    loggers.CreateLogger("PoRedoMedia.Browser"), Clip(error.Page, 300), Clip(error.Category, 200),
                    Clip(error.Message, 2000), Clip(error.Stack, 8000));
                PoTelemetry.ClientErrors.Add(1);
                return Results.NoContent();
            })
            .RequireRateLimiting(UploadRateLimit.ClientErrorPolicy);
        return app;
    }

    private static string Clip(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length > max ? text[..max] : text;
}
