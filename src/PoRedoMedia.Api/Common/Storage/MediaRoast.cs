using System.Text.Json;

namespace PoRedoMedia.Api.Common;

/// <summary>One roast line: when it starts (output time), how long its clip plays, and the clip.</summary>
public sealed record RoastLine(long TimestampMs, int DurationMs, string Text, string BlobPath);

/// <param name="AudioPath">The whole roast as one sound file.</param>
public sealed record RoastTrack(string Voice, string AudioPath, IReadOnlyList<RoastLine> Lines);

/// <summary>
/// The roast voiceover stored beside a media item (<c>roast.json</c> + its clips). Processing writes
/// it; the render mixes the clips in and the Export menu downloads the sound file.
/// </summary>
/// <remarks>
/// The lines are a track of their own, not cues in the director script: a cue's sound is a library
/// <see cref="SoundId"/>, and a script travels to other items through remixes, where a clip
/// generated for this video does not exist.
/// </remarks>
public static class MediaRoast
{
    public const string Comic = "Comic";
    public const string Neural = "Neural";
    public const string Rap = "Rap";

    /// <summary>Most speech one roast holds, however long the video is.</summary>
    public const int MaxTotalMs = 30_000;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The voice engines this server can perform, in menu order. Mock mode offers all three,
    /// each performed by the mock voice.
    /// </summary>
    public static IReadOnlyList<string> Voices(IConfiguration config, IHostEnvironment environment)
    {
        // The jokes are written by the chat deployment, so every voice needs Foundry. The key is
        // required too: the speech endpoints are called with it, not with a managed identity.
        if (MockAi.IsEnabled(config, environment))
            return [Comic, Neural, Rap];

        if (AiFoundryClient.Setting(config, "AiFoundry:Endpoint") is null
            || AiFoundryClient.Setting(config, "AiFoundry:Key") is null)
        {
            return [];
        }

        List<string> voices = [];
        if (AiFoundryClient.Setting(config, "AiFoundry:TtsDeployment") is not null) voices.Add(Comic);
        voices.Add(Neural);
        if (AiFoundryClient.Setting(config, "Google:ApiKey") is not null) voices.Add(Rap);
        return voices;
    }

    /// <summary>The session's roast, or null when it has none.</summary>
    public static async Task<RoastTrack?> LoadAsync(BlobStorageService blobs, MediaId mediaId, CancellationToken ct)
    {
        var path = MediaAnalysisPaths.Roast(mediaId.Value);
        if (!await blobs.ExistsAsync(path, ct))
            return null;

        await using var stream = await blobs.OpenReadAsync(path, ct);
        return await JsonSerializer.DeserializeAsync<RoastTrack>(stream, JsonOpts, ct);
    }

    public static async Task SaveAsync(BlobStorageService blobs, MediaId mediaId, RoastTrack track, CancellationToken ct)
    {
        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(track, JsonOpts));
        await blobs.UploadAsync(MediaAnalysisPaths.Roast(mediaId.Value), json, "application/json", ct);
    }
}
