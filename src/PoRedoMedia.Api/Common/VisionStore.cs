using System.Text.Json;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// A session's keyframes (<c>frames/</c> + <c>frames/timestamps.json</c>) and the vision labels
/// computed from them (<c>vision-labels.json</c>). Ingestion writes them when the frames are
/// uploaded; Processing reads them when the engine runs.
/// </summary>
public static class VisionStore
{
    /// <summary>Seconds between frames for sessions uploaded before frame timestamps were stored.</summary>
    public const double LegacyFrameIntervalSeconds = 3.0;

    /// <summary>
    /// Snake case, because the file predates the richer labels — older sessions carry only
    /// <c>timestamp_seconds</c> and <c>label</c>, and still load.
    /// </summary>
    private static readonly JsonSerializerOptions LabelJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Replaces the session's frames and their timestamps.</summary>
    public static async Task SaveFramesAsync(BlobStorageService blobs, MediaId mediaId, IReadOnlyList<KeyFrame> frames, CancellationToken ct)
    {
        await blobs.DeletePrefixAsync(SessionBlobPaths.FramesPrefix(mediaId.Value), ct);
        for (var i = 0; i < frames.Count; i++)
        {
            using var ms = new MemoryStream(frames[i].Image.ToArray());
            var extension = frames[i].MediaType == "image/png" ? "png" : "jpg";
            await blobs.UploadAsync(SessionBlobPaths.Frame(mediaId.Value, i, extension), ms, frames[i].MediaType, ct);
        }

        using var timestamps = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(frames.Select(f => f.TimestampSeconds)));
        await blobs.UploadAsync(SessionBlobPaths.FrameTimestamps(mediaId.Value), timestamps, "application/json", ct);
    }

    /// <summary>The stored frames in order; a frame that cannot be read is skipped.</summary>
    public static async Task<IReadOnlyList<KeyFrame>> LoadFramesAsync(BlobStorageService blobs, MediaId mediaId, CancellationToken ct)
    {
        var paths = new List<string>();
        await foreach (var path in blobs.ListBlobsByPrefixAsync(SessionBlobPaths.FramesPrefix(mediaId.Value), ct))
        {
            if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                paths.Add(path);
        }
        paths.Sort(StringComparer.Ordinal);

        var timestamps = await LoadTimestampsAsync(blobs, mediaId, ct);
        var frames = new List<KeyFrame>(paths.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            try
            {
                await using var stream = await blobs.OpenReadAsync(paths[i], ct);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                var t = i < timestamps.Length ? timestamps[i] : i * LegacyFrameIntervalSeconds;
                var mediaType = paths[i].EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                frames.Add(new KeyFrame(t, ms.ToArray(), mediaType));
            }
            catch (Azure.RequestFailedException)
            {
                // Deleted between the listing and the read; the rest of the frames still count.
            }
        }

        return frames;
    }

    public static async Task SaveLabelsAsync(BlobStorageService blobs, MediaId mediaId, IReadOnlyList<SceneLabel> labels, CancellationToken ct)
    {
        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(labels, LabelJsonOpts));
        await blobs.UploadAsync(SessionBlobPaths.VisionLabels(mediaId.Value), json, "application/json", ct);
    }

    /// <summary>The labels computed at upload, or none. Throws <see cref="JsonException"/> on a corrupt file.</summary>
    public static async Task<SceneLabel[]> LoadLabelsAsync(BlobStorageService blobs, MediaId mediaId, CancellationToken ct)
    {
        var path = SessionBlobPaths.VisionLabels(mediaId.Value);
        if (!await blobs.ExistsAsync(path, ct))
            return [];

        await using var stream = await blobs.OpenReadAsync(path, ct);
        return await JsonSerializer.DeserializeAsync<SceneLabel[]>(stream, LabelJsonOpts, ct) ?? [];
    }

    private static async Task<double[]> LoadTimestampsAsync(BlobStorageService blobs, MediaId mediaId, CancellationToken ct)
    {
        var path = SessionBlobPaths.FrameTimestamps(mediaId.Value);
        if (!await blobs.ExistsAsync(path, ct))
            return [];

        await using var stream = await blobs.OpenReadAsync(path, ct);
        return await JsonSerializer.DeserializeAsync<double[]>(stream, cancellationToken: ct) ?? [];
    }
}
