using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Media;

/// <summary>
/// What the browser worked out about one of the user's items and hands to the server: frames it
/// sampled from a video, and what its own speech model heard.
/// </summary>
public static class DeviceResultsEndpoints
{
    public static IEndpointRouteBuilder MapDeviceResults(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/media").RequireAntiforgeryValidation().RequireRateLimiting(UploadRateLimit.Policy);
        group.MapPost("/{id}/frames", UploadFramesAsync);
        group.MapPost("/{id}/transcript", SaveTranscriptAsync);
        return app;
    }

    /// <summary>
    /// Stores frames the browser sampled from a video and has them described. The labels are kept
    /// with the video, so every later run on it reuses them. A video whose frames are already
    /// described is not analysed again.
    /// </summary>
    private static async Task<Results<Ok<FramesResult>, NotFound, ProblemHttpResult>> UploadFramesAsync(
        MediaId id, FrameUploadRequest request, ClaimsPrincipal user, IMediaRepository media, BlobStorageService blobs,
        ISoundAssetRepository sounds, ILoggerFactory loggers, CancellationToken ct, [FromServices] IAiVisionService? vision = null,
        [FromServices] IClipVision? clipVision = null)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is not { Kind: MediaKind.Video, Status: MediaStatus.Ready })
            return TypedResults.NotFound();
        if (request.Frames is not { Count: > 0 and <= 400 } || request.Frames.Any(f => f is null || f.Length > 400_000))
            return MediaEndpoints.Refused("Send between 1 and 400 frames of at most 300 KB each.");

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
                return MediaEndpoints.Refused("A frame was not a valid image data URL.");
            }

            // The browser skips near-identical frames, so each frame carries its own time.
            frames.Add(new KeyFrame(request.Timestamps is { } times && i < times.Count ? Math.Max(0, times[i]) : i * 3.0, bytes, mediaType));
        }

        await VisionStore.SaveFramesAsync(blobs, id, frames, ct);
        SceneLabel[] labels = [];
        // With whole-clip vision the run watches the video itself, so describing frames here
        // would pay twice. The frames stay stored as its fallback.
        if (vision is not null && clipVision is null)
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
            return MediaEndpoints.Refused("Send between 1 and 2000 lines of speech.");

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
            return MediaEndpoints.Refused("None of the lines had words and a time.");

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
}
