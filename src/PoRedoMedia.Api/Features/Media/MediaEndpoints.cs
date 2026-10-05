using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;
using SixLabors.ImageSharp;

namespace PoRedoMedia.Api.Features.Media;

public static class MediaEndpoints
{
    private static readonly TimeSpan UploadLinkLifetime = TimeSpan.FromMinutes(15);

    public static IEndpointRouteBuilder MapMedia(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/media").RequireAntiforgeryValidation();
        group.MapPost("/sas", StartUploadAsync);
        group.MapPost("/{id}/confirm", ConfirmUploadAsync);
        group.MapPost("/{id}/frames", UploadFramesAsync);
        group.MapGet("/", ListAsync);
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
        var link = await storage.CreateUploadLinkAsync(item.SourcePath, expiresAt);
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

        var blob = storage.Blob(item.SourcePath);
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
            try
            {
                Image.Identify(await blobs.ReadAllBytesAsync(item.SourcePath, ct));
            }
            catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
            {
                problem = "That file is not a readable image.";
            }
        }
        else
        {
            // ffprobe reads the headers straight from storage; the video is never copied here.
            duration = await ffmpeg.DurationSecondsAsync(storage.CreateReadLink(item.SourcePath, TimeSpan.FromMinutes(5)).ToString(), ct);
            if (duration <= 0)
                problem = "That file is not a readable video.";
            else if (duration > UploadValidation.MaxVideoSeconds)
                problem = "Videos can be up to 10 minutes long.";
        }

        if (problem is not null)
        {
            await RemoveAsync(item, media, blobs, links, ct);
            return Refused(problem);
        }

        item = item with { Status = MediaStatus.Ready, SizeBytes = size, DurationSeconds = duration };
        await thumbnails.CreateAsync(item, ct);
        await media.SaveAsync(item, ct);

        // Start decoding the video's sound now, so it is usually ready by the time a run needs it.
        if (item.Kind == MediaKind.Video)
            audio.Prefetch(item.Id, item.SourcePath);
        return TypedResults.Ok(item.ToDto());
    }

    /// <summary>
    /// Stores frames the browser sampled from a video and has them described. The labels are kept
    /// with the video, so every later run on it reuses them. A video whose frames are already
    /// described is not analysed again.
    /// </summary>
    private static async Task<Results<Ok<FramesResult>, NotFound, ProblemHttpResult>> UploadFramesAsync(
        MediaId id, FrameUploadRequest request, ClaimsPrincipal user, IMediaRepository media, BlobStorageService blobs,
        IServiceProvider services, CancellationToken ct)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is not { Kind: MediaKind.Video, Status: MediaStatus.Ready })
            return TypedResults.NotFound();
        if (request.Frames is not { Count: > 0 and <= 400 } || request.Frames.Any(f => f is null || f.Length > 400_000))
            return Refused("Send between 1 and 400 frames of at most 300 KB each.");

        var known = await VisionStore.LoadLabelsAsync(blobs, id, ct);
        if (known.Length > 0)
            return TypedResults.Ok(new FramesResult(0, known.Length));

        var frames = new List<KeyFrame>();
        foreach (var i in EvenlySpaced(request.Frames.Count, MaxFrames))
        {
            var (bytes, mediaType) = DecodeDataUrl(request.Frames[i]);
            // The browser skips near-identical frames, so each frame carries its own time.
            frames.Add(new KeyFrame(request.Timestamps is { } times && i < times.Count ? Math.Max(0, times[i]) : i * 3.0, bytes, mediaType));
        }

        await VisionStore.SaveFramesAsync(blobs, id, frames, ct);
        SceneLabel[] labels = [];
        if (services.GetService<IAiVisionService>() is { } vision)
        {
            try
            {
                var sounds = await services.GetRequiredService<ISoundAssetRepository>().LoadAllAsync(ct);
                labels = await vision.AnalyseAsync(frames, SoundVocabulary.Tags(sounds), ct);
                await VisionStore.SaveLabelsAsync(blobs, id, labels, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The run retries vision on the stored frames, then falls back to placing by time.
                services.GetRequiredService<ILoggerFactory>().CreateLogger("Frames").LogWarning(ex, "Frame vision failed for {MediaId}", id);
            }
        }

        return TypedResults.Ok(new FramesResult(frames.Count, labels.Length));
    }

    /// <summary>Most frames one video sends to vision.</summary>
    private const int MaxFrames = 40;

    internal static IEnumerable<int> EvenlySpaced(int count, int max) =>
        count <= max ? Enumerable.Range(0, count) : Enumerable.Range(0, max).Select(i => (int)((long)i * count / max));

    internal static (byte[] Bytes, string MediaType) DecodeDataUrl(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        var header = comma >= 0 ? dataUrl[..comma] : string.Empty;
        var mediaType = header.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg"
            : header.StartsWith("data:image/webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
            : "image/png";
        return (Convert.FromBase64String(comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl), mediaType);
    }

    private static async Task<Ok<List<MediaDto>>> ListAsync(ClaimsPrincipal user, IMediaRepository media, CancellationToken ct) =>
        TypedResults.Ok((await media.ListAsync(UserId.From(user), ct))
            .Where(m => m.Status == MediaStatus.Ready)
            .Select(m => m.ToDto())
            .ToList());

    private static async Task<IResult> ContentAsync(
        MediaId id, bool? download, HttpContext http, IMediaRepository media, CancellationToken ct) =>
        await media.GetAsync(UserId.From(http.User), id, ct) is { Status: MediaStatus.Ready } item
            ? await BlobDelivery.ServeAsync(http, item.SourcePath, download == true ? DownloadName(item) : null, ct)
            : Results.NotFound();

    private static async Task<IResult> ThumbAsync(MediaId id, HttpContext http, IMediaRepository media, CancellationToken ct) =>
        await media.GetAsync(UserId.From(http.User), id, ct) is { Status: MediaStatus.Ready }
            ? await BlobDelivery.ServeAsync(http, MediaBlobPaths.Thumbnail(id), ct: ct)
            : Results.NotFound();

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

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        MediaId id, ClaimsPrincipal user, IMediaRepository media, BlobStorageService blobs, IShareLinks links, CancellationToken ct)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is null)
            return TypedResults.NotFound();

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

    private static ProblemHttpResult Refused(string reason) =>
        TypedResults.Problem(detail: reason, statusCode: StatusCodes.Status400BadRequest);
}
