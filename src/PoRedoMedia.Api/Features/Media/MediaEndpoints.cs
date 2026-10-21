using System.IO.Compression;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Media;

public static class MediaEndpoints
{
    internal static readonly TimeSpan UploadLinkLifetime = TimeSpan.FromMinutes(15);

    public static IEndpointRouteBuilder MapMedia(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/media").RequireAntiforgeryValidation();
        group.MapPost("/sas", StartUploadAsync).RequireRateLimiting(UploadRateLimit.Policy);
        group.MapPost("/{id}/confirm", ConfirmUploadAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/zip", ZipAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapGet("/{id}/content", ContentAsync);
        group.MapGet("/{id}/thumb", ThumbAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        return app;
    }

    /// <summary>Reserves a media item and returns a link the browser uploads the file to directly.</summary>
    private static async Task<Results<Ok<UploadTicket>, ProblemHttpResult>> StartUploadAsync(
        UploadRequest request, ClaimsPrincipal user, IMediaRepository media, StorageClients storage, CancellationToken ct)
    {
        var type = UploadValidation.Classify(request.FileName, request.SizeBytes);
        if (type.Error is not null)
            return Refused(type.Error);

        var item = new MediaItem
        {
            Owner = UserId.From(user),
            Id = MediaId.New(),
            Kind = type.Kind,
            Status = MediaStatus.Uploading,
            Origin = "Upload",
            Title = Path.GetFileName(request.FileName),
            ContentType = type.ContentType,
            Extension = type.Extension,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await media.SaveAsync(item, ct);

        var expiresAt = DateTimeOffset.UtcNow.Add(UploadLinkLifetime);
        var link = await storage.CreateUploadLinkAsync(MediaBlobPaths.Upload(item.Id, item.Extension), expiresAt);
        return TypedResults.Ok(new UploadTicket(item.Id.Value, link.ToString(), expiresAt));
    }

    /// <summary>
    /// Checks what actually arrived. The upload link cannot limit size or content, so nothing the
    /// client declared is trusted here: size, image format and video length are all measured.
    /// </summary>
    private static async Task<Results<Ok<MediaDto>, NotFound, ProblemHttpResult>> ConfirmUploadAsync(
        MediaId id, ClaimsPrincipal user, IMediaRepository media, StorageClients storage, BlobStorageService blobs,
        FFmpegProcess ffmpeg, Thumbnails thumbnails, ISourceAudioAnalysis audio, IShareLinks links, CancellationToken ct)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is null || item.Status != MediaStatus.Uploading)
            return TypedResults.NotFound();

        var uploadPath = MediaBlobPaths.Upload(item.Id, item.Extension);
        var blob = storage.Blob(uploadPath);
        if (!await blob.ExistsAsync(ct))
            return Refused("The file has not been uploaded yet.");

        var size = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength;
        double? duration = null;
        string? problem = null;

        if (size > UploadValidation.MaxBytes(item.Kind))
        {
            problem = item.Kind == MediaKind.Video ? "Videos can be up to 200 MB." : "Images can be up to 10 MB.";
        }
        else if (item.Kind == MediaKind.Image)
        {
            // The header alone gives the size, so a small file that unpacks to gigabytes of
            // pixels is refused before anything decodes it.
            if (ImageBytes.Identify(await blobs.ReadAllBytesAsync(uploadPath, ct)) is not { } dimensions)
                problem = "That file is not a readable image.";
            else if ((long)dimensions.Width * dimensions.Height > UploadValidation.MaxImagePixels)
                problem = "Images can be up to 40 megapixels.";
        }
        else
        {
            // ffprobe reads the headers straight from storage; the video is never copied here.
            duration = await ffmpeg.DurationSecondsAsync(storage.CreateReadLink(uploadPath, TimeSpan.FromMinutes(5)).ToString(), ct);
            if (duration <= 0 && item.Extension == ".webm")
                (duration, size) = await RepackAsync(uploadPath, item, storage, blobs, ffmpeg, size, ct);
            if (duration <= 0)
                problem = "That file is not a readable video.";
            else if (duration > UploadValidation.MaxVideoSeconds + UploadValidation.VideoSecondsSlack)
                problem = "Videos can be up to 1 minute long.";
        }

        if (problem is not null)
        {
            await RemoveAsync(item, media, blobs, links, ct);
            return Refused(problem);
        }

        // The checked bytes are copied inside storage to the path everything else reads, with the
        // content type this app chose rather than the one the browser sent. The upload link can
        // still write for a few minutes, but only to a blob nothing reads any more.
        var source = storage.Blob(item.SourcePath);
        await (await source.StartCopyFromUriAsync(blob.Uri, cancellationToken: ct)).WaitForCompletionAsync(ct);
        await source.SetHttpHeadersAsync(new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = item.ContentType }, cancellationToken: ct);
        await blob.DeleteIfExistsAsync(cancellationToken: ct);

        item = item with { Status = MediaStatus.Ready, SizeBytes = size, DurationSeconds = duration };
        await thumbnails.CreateAsync(item, ct);
        await media.SaveAsync(item, ct);

        // Start decoding the video's sound now, so it is usually ready by the time a run needs it.
        if (item.Kind == MediaKind.Video)
            audio.Prefetch(item.Id, item.SourcePath);
        return TypedResults.Ok(item.ToDto());
    }

    /// <summary>
    /// A clip recorded in the browser is a WebM written while it was being captured: its header
    /// carries no length, so it cannot be measured from storage or scrubbed in a player. Copying
    /// its streams into a fresh file, without re-encoding, gives it both. Returns the length and
    /// size of the repacked file, or a length of 0 when the file was not a video after all.
    /// </summary>
    private static async Task<(double Duration, long Size)> RepackAsync(
        string uploadPath, MediaItem item, StorageClients storage, BlobStorageService blobs, FFmpegProcess ffmpeg, long size, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"poredomedia-repack-{item.Id}.webm");
        try
        {
            var link = storage.CreateReadLink(uploadPath, TimeSpan.FromMinutes(10));
            if (await ffmpeg.RunAsync($"-v error -i \"{link}\" -c copy -y \"{file}\"", item.Id, ct) != 0 || !File.Exists(file))
                return (0, size);

            var duration = await ffmpeg.DurationSecondsAsync(file, ct);
            if (duration <= 0)
                return (0, size);

            await blobs.UploadFileAsync(uploadPath, file, item.ContentType, ct);
            return (duration, new FileInfo(file).Length);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private static async Task<Ok<List<MediaDto>>> ListAsync(ClaimsPrincipal user, IMediaRepository media, CancellationToken ct) =>
        TypedResults.Ok((await media.ListAsync(UserId.From(user), ct))
            .Where(m => m.Status == MediaStatus.Ready)
            .Select(m => m.ToDto())
            .ToList());

    private static async Task<Results<Ok<MediaDto>, NotFound>> GetAsync(MediaId id, ClaimsPrincipal user, IMediaRepository media, CancellationToken ct) =>
        await media.GetAsync(UserId.From(user), id, ct) is { Status: MediaStatus.Ready } item
            ? TypedResults.Ok(item.ToDto())
            : TypedResults.NotFound();

    private static async Task<IResult> ContentAsync(
        MediaId id, bool? download, HttpContext http, IMediaRepository media, BlobDelivery delivery, CancellationToken ct) =>
        await media.GetAsync(UserId.From(http.User), id, ct) is { Status: MediaStatus.Ready } item
            ? await delivery.ServeAsync(http, item.SourcePath, download == true ? DownloadName(item) : null, ct: ct)
            : Results.NotFound();

    private static async Task<IResult> ThumbAsync(MediaId id, HttpContext http, IMediaRepository media, BlobDelivery delivery, CancellationToken ct) =>
        await media.GetAsync(UserId.From(http.User), id, ct) is { Status: MediaStatus.Ready }
            ? await delivery.ServeAsync(http, MediaBlobPaths.Thumbnail(id), ct: ct)
            : Results.NotFound();

    /// <summary>Most items one ZIP holds.</summary>
    private const int MaxZipItems = 50;

    /// <summary>
    /// Several of the user's items as one ZIP, written straight to the response. Nothing is
    /// compressed again: pictures and video already are, and the hosting plan's CPU is scarce.
    /// </summary>
    private static async Task ZipAsync(string ids, HttpContext http, IMediaRepository media, BlobStorageService blobs, CancellationToken ct)
    {
        var owner = UserId.From(http.User);
        var items = new List<MediaItem>();
        foreach (var raw in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(MaxZipItems))
        {
            if (MediaId.TryParse(raw, null, out var id) && await media.GetAsync(owner, id, ct) is { Status: MediaStatus.Ready } item)
                items.Add(item);
        }

        if (items.Count == 0)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // The archive writer still makes a few small synchronous writes (entry headers), even
        // through its async methods, and the server refuses those unless told otherwise.
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } body)
            body.AllowSynchronousIO = true;

        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = "attachment; filename=\"poredomedia-gallery.zip\"";
        await using var zip = await ZipArchive.CreateAsync(http.Response.Body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var name = DownloadName(item);
            for (var n = 2; !used.Add(name); n++)
                name = $"{Path.GetFileNameWithoutExtension(DownloadName(item))} ({n}){item.Extension}";

            await using var entry = await zip.CreateEntry(name, CompressionLevel.NoCompression).OpenAsync(ct);
            await using var blob = await blobs.OpenReadAsync(item.SourcePath, ct);
            await blob.CopyToAsync(entry, ct);
        }
    }

    private static async Task<Results<Ok<MediaDto>, NotFound, ProblemHttpResult>> UpdateAsync(
        MediaId id, MediaUpdateRequest request, ClaimsPrincipal user, IMediaRepository media, CancellationToken ct)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is null)
            return TypedResults.NotFound();

        var title = request.Title?.Trim();
        if (title is { Length: 0 or > 120 })
            return Refused("A title must be 1 to 120 characters.");

        item = item with { Title = title ?? item.Title, Pinned = request.Pinned ?? item.Pinned };
        await media.SaveAsync(item, ct);
        return TypedResults.Ok(item.ToDto());
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        MediaId id, ClaimsPrincipal user, IMediaRepository media, BlobStorageService blobs, IShareLinks links,
        Features.Runs.RunDispatcher dispatcher, CancellationToken ct)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is null)
            return TypedResults.NotFound();
        // The run would fail half way, its credit spent, and write results under a deleted item.
        if (dispatcher.IsBusy(id))
            return TypedResults.Problem(detail: "This item has a run in progress. Delete it when the run ends.", statusCode: StatusCodes.Status409Conflict);

        await RemoveAsync(item, media, blobs, links, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Blobs first: if the row went first and the blob delete failed, nothing would point at the orphans.</summary>
    private static async Task RemoveAsync(MediaItem item, IMediaRepository media, BlobStorageService blobs, IShareLinks links, CancellationToken ct)
    {
        // A deleted item must not stay reachable through its link.
        if (item.ShareToken is not null)
            await links.RevokeAsync(item.ShareToken, ct);

        await blobs.DeletePrefixAsync(MediaBlobPaths.Prefix(item.Id), ct);
        await media.DeleteAsync(item.Owner, item.Id, ct);
    }

    private static string DownloadName(MediaItem item)
    {
        var name = string.Concat(Path.GetFileNameWithoutExtension(item.Title).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' '));
        return (name.Length == 0 ? "media" : name) + item.Extension;
    }

    internal static ProblemHttpResult Refused(string reason) =>
        TypedResults.Problem(detail: reason, statusCode: StatusCodes.Status400BadRequest);
}
