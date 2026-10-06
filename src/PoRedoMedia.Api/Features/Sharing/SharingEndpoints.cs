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
        app.MapGet("/api/feed", FeedAsync);

        // The public side. Each route re-checks the link, so a revoked one stops working at once.
        app.MapGet("/v/{token}", PageAsync).AllowAnonymous();
        app.MapGet("/v/{token}/content", (string token, HttpContext http, ShareLinkStore links, IMediaRepository media, BlobDelivery delivery, CancellationToken ct) =>
            ServeAsync(token, http, links, media, delivery, thumbnail: false, ct)).AllowAnonymous();
        app.MapGet("/v/{token}/thumb", (string token, HttpContext http, ShareLinkStore links, IMediaRepository media, BlobDelivery delivery, CancellationToken ct) =>
            ServeAsync(token, http, links, media, delivery, thumbnail: true, ct)).AllowAnonymous();
        return app;
    }

    /// <summary>Most items the feed lists.</summary>
    private const int FeedSize = 60;

    /// <summary>
    /// Trending order: remixes count most, views a little, and everything fades with age, so a
    /// new post can pass an old favourite.
    /// </summary>
    internal static double Trending(int views, int remixes, double ageHours) =>
        (remixes * 3 + views * 0.1 + 1) / Math.Pow(Math.Max(0, ageHours) + 2, 1.5);

    /// <summary>What signed-in users have posted, trending first or newest first.</summary>
    private static async Task<Ok<List<FeedItemDto>>> FeedAsync(
        string? sort, ClaimsPrincipal user, ShareLinkStore links, IMediaRepository media, CancellationToken ct)
    {
        var me = UserId.From(user);
        var now = DateTimeOffset.UtcNow;
        var posted = await links.ListFeedAsync(ct);
        var ordered = sort == "new"
            ? posted.OrderByDescending(l => l.SharedAt)
            : posted.OrderByDescending(l => Trending(l.Views, l.Remixes, (now - l.SharedAt).TotalHours));

        // ponytail: one read per listed item for its title and kind. Copy them onto the link row
        // if the feed ever outgrows a page of sixty.
        var feed = new List<FeedItemDto>();
        foreach (var link in ordered.Take(FeedSize))
        {
            if (await media.GetAsync(link.Owner, link.Media, ct) is { Status: MediaStatus.Ready } item && item.ShareToken == link.Token)
                feed.Add(new(link.Token, item.Title, item.Kind, link.Author, link.SharedAt, link.Views, link.Remixes, link.Owner == me, CanRemix(item)));
        }

        return TypedResults.Ok(feed);
    }

    /// <summary>Only a Meme-ify result carries cues another video can borrow.</summary>
    internal static bool CanRemix(MediaItem item) => item is { Kind: MediaKind.Video, Origin: nameof(MediaFunction.Memeify) };

    /// <summary>
    /// Returns the item's link, creating it on first use. Sharing twice gives the same link.
    /// <paramref name="feed"/> posts it to the feed or takes it off; absent leaves that as it is.
    /// </summary>
    private static async Task<Results<Ok<ShareLinkDto>, NotFound>> ShareAsync(
        MediaId id, bool? feed, ClaimsPrincipal user, HttpContext http, IMediaRepository media, ShareLinkStore links, IConfiguration configuration, CancellationToken ct)
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

        if (feed is { } onFeed)
        {
            // The name beside a post is the part of the sign-in name before the @, never the address.
            var author = (user.Identity?.Name ?? "someone").Split('@')[0];
            await links.SetFeedAsync(item.ShareToken, onFeed, author, ct);
        }

        var posted = feed ?? (await links.GetAsync(item.ShareToken, ct))?.OnFeed ?? false;
        return TypedResults.Ok(new ShareLinkDto($"{BaseUrl(http, configuration)}/v/{item.ShareToken}", posted));
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

        if (await SharedItemAsync(token, links, media, ct) is { } item && await links.GetAsync(token, ct) is { } link)
        {
            await links.CountViewAsync(token, ct);
            var home = BaseUrl(http, configuration);
            return Results.Content(
                SharePage.Render(item, $"{home}/v/{token}", link.Views + 1, link.Remixes, CanRemix(item) ? $"{home}/?remix={token}" : null),
                "text/html; charset=utf-8");
        }

        return Results.Content(SharePage.Shell("Link not available", "<h1>This link is no longer available</h1><p>It was turned off, or the item was deleted.</p>", ""),
            "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
    }
}
