namespace PoRedoMedia.Api.Common;

/// <summary>
/// Serves a stored blob by redirecting the browser to a short-lived read link. Proxying video
/// through the app would cost CPU on every byte and every seek, and the hosting plan allows
/// 60 CPU-minutes a day for everything.
/// </summary>
public static class BlobDelivery
{
    /// <summary>Long enough to watch a clip and scrub back through it; short enough to be useless if leaked.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public static async Task<IResult> ServeAsync(
        HttpContext http, string blobPath, string? downloadFileName = null, CancellationToken ct = default)
    {
        var services = http.RequestServices;
        if (!await services.GetRequiredService<BlobStorageService>().ExistsAsync(blobPath, ct))
            return Results.NotFound();

        var link = services.GetRequiredService<StorageClients>().CreateReadLink(blobPath, LinkLifetime, downloadFileName);
        // Private: the link is per-request and expires, so no shared cache may hold the redirect.
        http.Response.Headers.CacheControl = "private, max-age=300";
        return Results.Redirect(link.ToString());
    }
}
