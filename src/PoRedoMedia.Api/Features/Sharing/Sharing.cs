using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http.HttpResults;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Sharing;

/// <summary>
/// Share links: one row per token, pointing at one gallery item. Anyone holding the link can view
/// that item until the owner stops sharing it or deletes it.
/// </summary>
public sealed class ShareLinkStore(StorageClients storage) : IShareLinks
{
    private const string Partition = "share";
    private readonly Lazy<TableClient> _table = new(() => storage.Table(StorageNames.Tables.ShareLinks));

    /// <summary>12 characters from 62: about 71 bits, so a link cannot be guessed.</summary>
    internal static string NewToken() =>
        RandomNumberGenerator.GetString("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", 12);

    public Task AddAsync(string token, UserId owner, MediaId media, CancellationToken ct) =>
        _table.Value.AddEntityAsync(new TableEntity(Partition, token) { ["Owner"] = owner.Value, ["MediaId"] = media.ToString() }, ct);

    /// <summary>Whose item a token points at, or null when the token is malformed, unknown or revoked.</summary>
    public async Task<(UserId Owner, MediaId Media)?> ResolveAsync(string token, CancellationToken ct)
    {
        if (token is not { Length: 12 } || !token.All(char.IsAsciiLetterOrDigit))
            return null;

        var row = await _table.Value.GetEntityIfExistsAsync<TableEntity>(Partition, token, cancellationToken: ct);
        return row.HasValue ? (new UserId(row.Value!.GetString("Owner")), MediaId.Parse(row.Value.GetString("MediaId"), null)) : null;
    }

    public async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        try
        {
            await _table.Value.DeleteEntityAsync(Partition, token, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone.
        }
    }
}

public static class SharingEndpoints
{
    public static IEndpointRouteBuilder MapSharing(this IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/api/media/{id}/share").RequireAntiforgeryValidation();
        owner.MapPost("/", ShareAsync);
        owner.MapDelete("/", StopSharingAsync);

        // The public side. Each route re-checks the link, so a revoked one stops working at once.
        app.MapGet("/v/{token}", PageAsync).AllowAnonymous();
        app.MapGet("/v/{token}/content", (string token, HttpContext http, ShareLinkStore links, IMediaRepository media, CancellationToken ct) =>
            ServeAsync(token, http, links, media, thumbnail: false, ct)).AllowAnonymous();
        app.MapGet("/v/{token}/thumb", (string token, HttpContext http, ShareLinkStore links, IMediaRepository media, CancellationToken ct) =>
            ServeAsync(token, http, links, media, thumbnail: true, ct)).AllowAnonymous();
        return app;
    }

    /// <summary>Returns the item's link, creating it on first use. Sharing twice gives the same link.</summary>
    private static async Task<Results<Ok<ShareLinkDto>, NotFound>> ShareAsync(
        MediaId id, ClaimsPrincipal user, HttpContext http, IMediaRepository media, ShareLinkStore links, CancellationToken ct)
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

        return TypedResults.Ok(new ShareLinkDto($"{http.Request.Scheme}://{http.Request.Host}/v/{item.ShareToken}"));
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
        string token, HttpContext http, ShareLinkStore links, IMediaRepository media, bool thumbnail, CancellationToken ct) =>
        await SharedItemAsync(token, links, media, ct) is { } item
            ? await BlobDelivery.ServeAsync(http, thumbnail ? MediaBlobPaths.Thumbnail(item.Id) : item.SourcePath, ct: ct, lifetime: BlobDelivery.SharedLinkLifetime)
            : Results.NotFound();

    private static async Task<IResult> PageAsync(string token, HttpContext http, ShareLinkStore links, IMediaRepository media, CancellationToken ct)
    {
        // Never cached: a revoked link must stop working immediately, including in a shared cache.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src * data:; media-src *; style-src 'unsafe-inline'; frame-ancestors 'none'";

        return await SharedItemAsync(token, links, media, ct) is { } item
            ? Results.Content(Page(item, $"{http.Request.Scheme}://{http.Request.Host}/v/{token}"), "text/html; charset=utf-8")
            : Results.Content(Shell("Link not available", "<h1>This link is no longer available</h1><p>It was turned off, or the item was deleted.</p>", ""),
                "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
    }

    /// <summary>A self-contained page: link previewers and visitors never load the app itself.</summary>
    private static string Page(MediaItem item, string url)
    {
        var encode = HtmlEncoder.Default;
        var title = encode.Encode(item.Title);
        var body = item.Kind switch
        {
            MediaKind.Image => $"""<img src="{url}/content" alt="{title}">""",
            MediaKind.Video => $"""<video src="{url}/content" poster="{url}/thumb" controls playsinline preload="metadata"></video>""",
            _ => $"""<audio src="{url}/content" controls></audio>""",
        };
        if (item.Text is not null)
            body += $"<pre>{encode.Encode(item.Text)}</pre>";

        var preview = item.Kind == MediaKind.Audio ? "" : $"""<meta property="og:image" content="{url}/{(item.Kind == MediaKind.Image ? "content" : "thumb")}">""";
        if (item.Kind == MediaKind.Video)
            preview += $"""<meta property="og:video" content="{url}/content"><meta property="og:video:type" content="video/mp4">""";

        return Shell(
            title, $"<h1>{title}</h1>{body}",
            $"""<meta property="og:title" content="{title}"><meta property="og:url" content="{url}"><meta property="og:site_name" content="PoRedoMedia">{preview}""");
    }

    private static string Shell(string title, string body, string head) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>{{title}} · PoRedoMedia</title>
        {{head}}
        <style>
        body { margin: 0; padding: 1.5rem 1rem; background: #f4f5f7; color: #16181d; font: 16px/1.5 system-ui, sans-serif; }
        main { max-width: 960px; margin: 0 auto; }
        h1 { font-size: 1.4rem; overflow-wrap: anywhere; }
        img, video { max-width: 100%; max-height: 80vh; border-radius: 6px; background: #e3e6eb; }
        audio { width: 100%; }
        pre { white-space: pre-wrap; font: inherit; }
        footer { margin-top: 2rem; color: #4b5563; }
        </style>
        </head>
        <body><main>{{body}}<footer>Made with PoRedoMedia</footer></main></body>
        </html>
        """;
}
