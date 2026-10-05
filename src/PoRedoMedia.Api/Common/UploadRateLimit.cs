using System.Security.Claims;
using System.Threading.RateLimiting;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// A per-user ceiling on the endpoints that store a file or call an AI provider without spending
/// a run credit: upload links, video frames and sound uploads.
/// </summary>
public static class UploadRateLimit
{
    public const string Policy = "uploads";

    // ponytail: counted in memory, so it resets when the app restarts and is per instance. Move
    // the count into the quota table if abuse survives that.
    public static IServiceCollection AddUploadRateLimit(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Policy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromHours(1) }));
        });
}
