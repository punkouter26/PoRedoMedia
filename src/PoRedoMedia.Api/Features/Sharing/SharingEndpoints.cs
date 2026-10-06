using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Sharing;

public static class SharingEndpoints
{
    public static IEndpointRouteBuilder MapSharing(this IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/api/media/{id}/share").RequireAntiforgeryValidation();
        owner.MapPost("/", ShareAsync);
        owner.MapDelete("/", StopSharingAsync);

        // The public side. Each route re-checks the link, so a revoked one stops working at once.
        app.MapGet("/v/{token}", PageAsync).AllowAnonymous();
        app.MapGet("/v/{token}/content", (string token, HttpContext http, ShareLinkStore links, IMediaRepository media, BlobDelivery delivery, CancellationToken ct) =>
            ServeAsync(token, http, links, media, delivery, thumbnail: false, ct)).AllowAnonymous();
        app.MapGet("/v/{token}/thumb", (string token, HttpContext http, ShareLinkStore links, IMediaRepository media, BlobDelivery delivery, CancellationToken ct) =>
            ServeAsync(token, http, links, media, delivery, thumbnail: true, ct)).AllowAnonymous();
        return app;
    }

    /// <summary>Returns the item's link, creating it on first use. Sharing twice gives the same link.</summary>
    private static async Task<Results<Ok<ShareLinkDto>, NotFound>> ShareAsync(
        MediaId id, ClaimsPrincipal user, HttpContext http, IMediaRepository media, ShareLinkStore links, IConfiguration configuration, CancellationToken ct)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is not { Status: MediaStatus.Ready })
            return TypedResults.NotFound();

        if (item.ShareToken is null)
        {
            item = item with { ShareToken = ShareLinkStore.NewToken() };
            await links.AddAsync(item.ShareToken, item.Owner, item.Id, ct);
            await media.SaveAsync(item, ct);
        }

        return TypedResults.Ok(new ShareLinkDto($"{BaseUrl(http, configuration)}/v/{item.ShareToken}"));
    }

    private static async Task<Results<NoContent, NotFound>> StopSharingAsync(
        MediaId id, ClaimsPrincipal user, IMediaRepository media, ShareLinkStore links, CancellationToken ct)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is null)
            return TypedResults.NotFound();

        if (item.ShareToken is not null)
        {
            // The link row goes first: it is what the public routes check.
            await links.RevokeAsync(item.ShareToken, ct);
            await media.SaveAsync(item with { ShareToken = null }, ct);
        }

        return TypedResults.NoContent();
    }

    private static async Task<MediaItem?> SharedItemAsync(string token, ShareLinkStore links, IMediaRepository media, CancellationToken ct) =>
        await links.ResolveAsync(token, ct) is { } link
        && await media.GetAsync(link.Owner, link.Media, ct) is { Status: MediaStatus.Ready } item
        && item.ShareToken == token
            ? item
            : null;

    private static async Task<IResult> ServeAsync(
        string token, HttpContext http, ShareLinkStore links, IMediaRepository media, BlobDelivery delivery, bool thumbnail, CancellationToken ct) =>
        await SharedItemAsync(token, links, media, ct) is { } item
            ? await delivery.ServeAsync(http, thumbnail ? MediaBlobPaths.Thumbnail(item.Id) : item.SourcePath, lifetime: BlobDelivery.SharedLinkLifetime, ct: ct)
            : Results.NotFound();

    /// <summary>The configured public address when there is one: a Host header is the caller's to choose.</summary>
    private static string BaseUrl(HttpContext http, IConfiguration configuration) =>
        configuration[ConfigKeys.AppPublicBaseUrl] is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : $"{http.Request.Scheme}://{http.Request.Host}";

    private static async Task<IResult> PageAsync(
        string token, HttpContext http, ShareLinkStore links, IMediaRepository media, IConfiguration configuration, CancellationToken ct)
    {
        // Never cached: a revoked link must stop working immediately, including in a shared cache.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src * data:; media-src *; style-src 'unsafe-inline'; frame-ancestors 'none'";

        return await SharedItemAsync(token, links, media, ct) is { } item
            ? Results.Content(SharePage.Render(item, $"{BaseUrl(http, configuration)}/v/{token}"), "text/html; charset=utf-8")
            : Results.Content(SharePage.Shell("Link not available", "<h1>This link is no longer available</h1><p>It was turned off, or the item was deleted.</p>", ""),
                "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
    }
}
