using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Quota;

public static class QuotaEndpoints
{
    public static IEndpointRouteBuilder MapQuota(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/quota", GetAsync);
        return app;
    }

    private static async Task<Ok<QuotaStatusDto>> GetAsync(ClaimsPrincipal user, IRenderQuota quota, CancellationToken ct) =>
        TypedResults.Ok(await quota.GetStatusAsync(UserId.From(user), ct));
}
