using System.Text.Json;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// The trimmed speech transcript stored beside a session (<c>transcript.json</c>). Processing
/// writes it; the render (burned-in subtitles), the studio (subtitle preview, SRT export) and
/// script revision read it.
/// </summary>
public static class SessionTranscript
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>The segments, or an empty list when the session has none (no speech, no model, older session).</summary>
    public static async Task<List<TranscriptSegmentDto>> LoadAsync(BlobStorageService blobs, MediaId mediaId, CancellationToken ct)
    {
        var path = SessionBlobPaths.Transcript(mediaId.Value);
        if (!await blobs.ExistsAsync(path, ct))
            return [];

        await using var stream = await blobs.OpenReadAsync(path, ct);
        return await JsonSerializer.DeserializeAsync<List<TranscriptSegmentDto>>(stream, JsonOpts, ct) ?? [];
    }

    public static async Task SaveAsync(BlobStorageService blobs, MediaId mediaId, IReadOnlyList<TranscriptSegmentDto> segments, CancellationToken ct)
    {
        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(segments, JsonOpts));
        await blobs.UploadAsync(SessionBlobPaths.Transcript(mediaId.Value), json, "application/json", ct);
    }
}
