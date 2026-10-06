using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Common.Ai;

// Mock providers for the video functions, registered when MockAi.IsEnabled. None makes a network
// call. The roast voice uses the local ffmpeg to make a real, audible clip.

/// <summary>Reports the same moments for every video, early enough to land inside a short clip.</summary>
public sealed class MockFrameVision : IAiVisionService
{
    private static readonly SceneLabel[] Labels =
    [
        new(0.5, "surprise") { Energy = 0.8, SubjectX = 0.5, SubjectY = 0.4 },
        new(1.5, "fall") { Energy = 0.8 },
        new(3.0, "explosion") { Energy = 0.9, SubjectX = 0.5, SubjectY = 0.4 },
        new(6.5, "celebration") { Energy = 0.7 },
        new(9.0, "scream") { Energy = 0.9, SubjectX = 0.45, SubjectY = 0.3 },
        new(12.0, "awkward silence") { Energy = 0.2 },
    ];

    public Task<SceneLabel[]> AnalyseAsync(
        IReadOnlyList<KeyFrame> frames, IReadOnlyCollection<string> tagVocabulary, CancellationToken cancellationToken = default) =>
        Task.FromResult(Labels);
}

/// <summary>Writes one cue per moment, cycling through the effects and captions.</summary>
public sealed class MockDirector : IDirectorService
{
    // SnapZoom is retired: it zoomed the whole video, not the cue's own moment.
    private static readonly VisualEffectType[] Effects =
        [.. Enum.GetValues<VisualEffectType>().Where(e => e is not (VisualEffectType.None or VisualEffectType.SnapZoom))];

    public Task<DirectedScript> DirectAsync(
        SceneLabel[] labels, IReadOnlyList<SoundAsset> topCandidates, MediaId mediaId, bool hasRealVisionData = false,
        DirectorContext? context = null, CancellationToken cancellationToken = default)
    {
        if (topCandidates.Count == 0)
            return Task.FromResult(new DirectedScript([], "Mock title"));

        var entries = labels.Select((label, i) =>
        {
            var sound = topCandidates[i % topCandidates.Count];
            var effect = Effects[i % Effects.Length];
            return new ScriptEntry
            {
                EntryId = EntryId.New(),
                MediaId = mediaId,
                TimestampMs = (long)Math.Round(label.TimestampSeconds * 1000),
                SoundId = sound.SoundId,
                SoundName = sound.DisplayName,
                ActionVectorTags = [label.Label],
                SceneDescription = $"Mock scene: {label.Label}.",
                SelectionRationale = $"[MOCK] '{label.Label}' paired with '{sound.DisplayName}'.",
                VisualEffect = effect,
                OverlayAssetId = effect == VisualEffectType.Overlay ? "deal-with-it" : null,
                CaptionText = i == 0 ? "MOCK CAPTION" : null,
                CaptionPosition = "Top",
                PlacementType = PlacementType.Triggered,
            };
        }).ToArray();
        return Task.FromResult(new DirectedScript(entries, "Mock title"));
    }
}

/// <summary>Reports two fixed lines of speech for any audio.</summary>
public sealed class MockTranscription : ITranscriptionService
{
    public bool IsEnabled => true;

    public Task<IReadOnlyList<TranscriptSegmentDto>> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TranscriptSegmentDto>>(
        [
            new TranscriptSegmentDto(0.2, 1.4, "This is a mock transcript."),
            new TranscriptSegmentDto(1.5, 2.8, "No speech model was called."),
        ]);
}

/// <summary>"Speaks" two fixed lines as short tones, so a mock render still has a roast track to mix.</summary>
public sealed class MockVideoRoast(FFmpegProcess ffmpeg, BlobStorageService blobs) : IVideoRoast
{
    public async Task<RoastTrack?> GenerateAsync(
        MediaId mediaId, string voice, IReadOnlyList<SceneLabel> labels, IReadOnlyList<TranscriptSegmentDto> transcript,
        double durationSeconds, CancellationToken ct)
    {
        var clip = Path.Combine(Path.GetTempPath(), $"poredomedia-mockroast-{Guid.NewGuid():N}.mp3");
        try
        {
            if (await ffmpeg.RunAsync($"-y -f lavfi -i sine=frequency=330:duration=0.4 -c:a libmp3lame \"{clip}\"", mediaId, ct) != 0)
                throw new InvalidOperationException("The mock roast voice could not be made. Is ffmpeg installed?");

            var path = MediaAnalysisPaths.RoastClip(mediaId.Value, 0, "mp3");
            await blobs.UploadFileAsync(path, clip, "audio/mpeg", ct);
            long second = (long)(Math.Max(0.6, durationSeconds / 2) * 1000);
            return new RoastTrack(voice, path,
            [
                new RoastLine(100, 400, "Mock insult one.", path),
                new RoastLine(Math.Min(second, Math.Max(600, (long)(durationSeconds * 1000) - 500)), 400, "Mock insult two.", path),
            ]);
        }
        finally
        {
            File.Delete(clip);
        }
    }
}
