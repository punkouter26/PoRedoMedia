namespace PoRedoMedia.Api.Common;

/// <summary>
/// Serves a stored blob by redirecting the browser to a short-lived read link. Proxying video
/// through the app would cost CPU on every byte and every seek, and the hosting plan allows
/// 60 CPU-minutes a day for everything.
/// </summary>
public sealed class BlobDelivery(BlobStorageService blobs, StorageClients storage)
{
    /// <summary>Long enough to watch a clip and scrub back through it; short enough to be useless if leaked.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    /// <summary>For public share links: stopping sharing should cut access within minutes, not an hour.</summary>
    public static readonly TimeSpan SharedLinkLifetime = TimeSpan.FromMinutes(15);

    public async Task<IResult> ServeAsync(
        HttpContext http, string blobPath, string? downloadFileName = null, TimeSpan? lifetime = null, CancellationToken ct = default)
    {
        if (!await blobs.ExistsAsync(blobPath, ct))
            return Results.NotFound();

        var link = storage.CreateReadLink(blobPath, lifetime ?? LinkLifetime, downloadFileName);
        // Private: the link is per-request and expires, so no shared cache may hold the redirect.
        http.Response.Headers.CacheControl = "private, max-age=300";
        return Results.Redirect(link.ToString());
    }
}
