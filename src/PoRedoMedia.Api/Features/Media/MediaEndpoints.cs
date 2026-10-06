using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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
        group.MapPost("/sas", StartUploadAsync).RequireRateLimiting(UploadRateLimit.Policy);
        group.MapPost("/{id}/confirm", ConfirmUploadAsync);
        group.MapPost("/{id}/frames", UploadFramesAsync).RequireRateLimiting(UploadRateLimit.Policy);
        group.MapPost("/{id}/transcript", SaveTranscriptAsync).RequireRateLimiting(UploadRateLimit.Policy);
        group.MapGet("/", ListAsync);
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
            try
            {
                // The header alone gives the size, so a small file that unpacks to gigabytes of
                // pixels is refused before anything decodes it.
                var info = Image.Identify(await blobs.ReadAllBytesAsync(uploadPath, ct));
                if ((long)info.Width * info.Height > UploadValidation.MaxImagePixels)
                    problem = "Images can be up to 40 megapixels.";
            }
            catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
            {
                problem = "That file is not a readable image.";
            }
        }
        else
        {
            // ffprobe reads the headers straight from storage; the video is never copied here.
            duration = await ffmpeg.DurationSecondsAsync(storage.CreateReadLink(uploadPath, TimeSpan.FromMinutes(5)).ToString(), ct);
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
    /// Stores frames the browser sampled from a video and has them described. The labels are kept
    /// with the video, so every later run on it reuses them. A video whose frames are already
    /// described is not analysed again.
    /// </summary>
    private static async Task<Results<Ok<FramesResult>, NotFound, ProblemHttpResult>> UploadFramesAsync(
        MediaId id, FrameUploadRequest request, ClaimsPrincipal user, IMediaRepository media, BlobStorageService blobs,
        ISoundAssetRepository sounds, ILoggerFactory loggers, CancellationToken ct, [FromServices] IAiVisionService? vision = null)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is not { Kind: MediaKind.Video, Status: MediaStatus.Ready })
            return TypedResults.NotFound();
        if (request.Frames is not { Count: > 0 and <= 400 } || request.Frames.Any(f => f is null || f.Length > 400_000))
            return Refused("Send between 1 and 400 frames of at most 300 KB each.");

        // Frames are accepted once per video. If describing them failed, the run retries on the
        // stored frames; sending them again here would be a free, repeatable vision call.
        var known = await VisionStore.LoadLabelsAsync(blobs, id, ct);
        if (known.Length > 0 || (await VisionStore.LoadFramesAsync(blobs, id, ct)).Count > 0)
            return TypedResults.Ok(new FramesResult(0, known.Length));

        var frames = new List<KeyFrame>();
        foreach (var i in EvenlySpaced(request.Frames.Count, MaxFrames))
        {
            byte[] bytes;
            string mediaType;
            try
            {
                (bytes, mediaType) = DecodeDataUrl(request.Frames[i]);
            }
            catch (FormatException)
            {
                return Refused("A frame was not a valid image data URL.");
            }

            // The browser skips near-identical frames, so each frame carries its own time.
            frames.Add(new KeyFrame(request.Timestamps is { } times && i < times.Count ? Math.Max(0, times[i]) : i * 3.0, bytes, mediaType));
        }

        await VisionStore.SaveFramesAsync(blobs, id, frames, ct);
        SceneLabel[] labels = [];
        if (vision is not null)
        {
            try
            {
                var library = (await sounds.LoadAllAsync(ct)).VisibleTo(UserId.From(user));
                labels = await vision.AnalyseAsync(frames, SoundVocabulary.Tags(library), ct);
                await VisionStore.SaveLabelsAsync(blobs, id, labels, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The run retries vision on the stored frames, then falls back to placing by time.
                loggers.CreateLogger("Frames").LogWarning(ex, "Frame vision failed for {MediaId}", id);
            }
        }

        return TypedResults.Ok(new FramesResult(frames.Count, labels.Length));
    }

    /// <summary>
    /// Keeps what the browser's own speech model heard in a video, for a server that has no
    /// speech model. It is stored where the server's transcript would be, so captions and the
    /// director use it unchanged. Accepted once per video, and ignored when the server transcribes.
    /// </summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> SaveTranscriptAsync(
        MediaId id, List<TranscriptSegmentDto> segments, ClaimsPrincipal user, IMediaRepository media, BlobStorageService blobs,
        ITranscriptionService transcription, CancellationToken ct)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is not { Kind: MediaKind.Video, Status: MediaStatus.Ready })
            return TypedResults.NotFound();
        if (segments is not { Count: > 0 and <= 2000 })
            return Refused("Send between 1 and 2000 lines of speech.");

        var path = MediaAnalysisPaths.SourceSpeech(id.Value);
        if (transcription.IsEnabled || await blobs.ExistsAsync(path, ct))
            return TypedResults.NoContent();

        var length = item.DurationSeconds ?? UploadValidation.MaxVideoSeconds;
        var lines = segments
            .Where(s => s is not null && double.IsFinite(s.StartSeconds) && double.IsFinite(s.EndSeconds))
            .Select(s => (Start: Math.Clamp(s.StartSeconds, 0, length), End: Math.Clamp(s.EndSeconds, 0, length), Text: UserText.Clean(s.Text, 300)))
            .Where(s => s.Text is not null && s.End > s.Start)
            .Select(s => new TranscriptSegmentDto(s.Start, s.End, s.Text!))
            .OrderBy(s => s.StartSeconds)
            .ToList();
        if (lines.Count == 0)
            return Refused("None of the lines had words and a time.");

        await blobs.UploadAsync(
            path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(lines, System.Text.Json.JsonSerializerOptions.Web),
            "application/json", ct);
        return TypedResults.NoContent();
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

    private static ProblemHttpResult Refused(string reason) =>
        TypedResults.Problem(detail: reason, statusCode: StatusCodes.Status400BadRequest);
}
