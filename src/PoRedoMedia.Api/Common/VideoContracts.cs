// Cross-slice service contracts. Implementations live inside their owning slice; consumers
// resolve the interface from DI so no slice takes a compile-time dependency on a sibling.
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Common;

/// <summary>One sampled video frame: when it was taken (source time) and its encoded image.</summary>
public sealed record KeyFrame(double TimestampSeconds, ReadOnlyMemory<byte> Image, string MediaType);

/// <summary>
/// What happens at one moment of the clip — from vision, or a speech line. Only the label is
/// required; the rest sharpens matching (tags), placement (energy) and sticker position (subject).
/// </summary>
public sealed record SceneLabel(double TimestampSeconds, string Label)
{
    /// <summary>0 (calm) to 1 (chaos): how strongly this moment earns a sound.</summary>
    public double Energy { get; init; } = 0.5;

    /// <summary>Tags picked from the sound library's own vocabulary, so matching needs no translation.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Centre of the main subject (usually a face) as 0–1 of the frame; null when there is none.</summary>
    public double? SubjectX { get; init; }

    public double? SubjectY { get; init; }
}

public interface IAiVisionService
{
    /// <summary>
    /// Labels each keyframe. <paramref name="tagVocabulary"/> is the sound library's tag set; vision
    /// picks tags from it so a scene maps onto sounds without a second model call.
    /// </summary>
    Task<SceneLabel[]> AnalyseAsync(
        IReadOnlyList<KeyFrame> frames,
        IReadOnlyCollection<string> tagVocabulary,
        CancellationToken cancellationToken = default);
}

public interface IDirectorService
{
    /// <summary>
    /// Given scene labels and the sound menu, produces one script entry per label with placement
    /// rationale, captions and effects, plus a title for the video. An entry's timestamp is its
    /// label's timestamp.
    /// </summary>
    Task<DirectedScript> DirectAsync(
        SceneLabel[] labels,
        IReadOnlyList<SoundAsset> topCandidates,
        MediaId mediaId,
        bool hasRealVisionData = false,
        DirectorContext? context = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The director's answer: the cues, and a title for the video when it offered one.</summary>
public sealed record DirectedScript(ScriptEntry[] Entries, string? Title = null);

/// <summary>
/// Everything the director knows about a run beyond the scene labels: who is directing (persona),
/// what is being said (speech transcript) and which sounds this user has starred.
/// </summary>
public sealed record DirectorContext(
    string? MemePersona,
    IReadOnlyList<TranscriptSegmentDto> Transcript,
    IReadOnlySet<SoundId> Favorites)
{
    public static readonly DirectorContext Empty = new(null, [], new HashSet<SoundId>());
}

/// <summary>A sound scored against the current scene, highest score first.</summary>
public sealed record SoundCandidate(SoundAsset Sound, float Score);

/// <summary>Ranks the sound library against a vision label. Implemented by the MemeLibrary slice.</summary>
public interface ISemanticMatchingService
{
    /// <summary>For each query, the best-matching sounds in <paramref name="library"/>, best first.</summary>
    IReadOnlyList<IReadOnlyList<SoundCandidate>> GetTopCandidatesBatch(
        IReadOnlyList<SoundAsset> library, IReadOnlyList<string> queries, int topN = 3);
}

/// <summary>
/// Media operations other slices need from the ffmpeg toolchain the Output slice owns.
/// </summary>
public interface IMediaToolkit
{
    /// <summary>
    /// Decodes the whole source's audio in one ffmpeg pass: writes a speech-ready MP3 (mono 16 kHz)
    /// to <paramref name="speechDestinationPath"/> when one is given, and returns the loudness
    /// envelope in source time together with the source's real length.
    /// </summary>
    Task<SourceMedia> AnalyseSourceAudioAsync(
        string sourceBlobPath,
        string? speechDestinationPath,
        CancellationToken cancellationToken = default);
}

/// <summary>What ffprobe and one decode say about a source upload.</summary>
/// <param name="DurationSeconds">The container's duration; 0 when ffprobe could not read one.</param>
/// <param name="Envelope">Null when the source has no audio stream.</param>
public sealed record SourceMedia(double DurationSeconds, AudioEnvelope? Envelope);

/// <summary>RMS loudness per frame of audio, <see cref="FramesPerSecond"/> frames per second.</summary>
public sealed record AudioEnvelope(float[] Rms, int FramesPerSecond);

/// <summary>
/// The source's audio, analysed once and shared by transcription, cue snapping and captions.
/// Implemented by the Processing slice; Ingestion starts it as soon as the upload is confirmed.
/// </summary>
public interface ISourceAudioAnalysis
{
    /// <summary>Starts analysing the item's source audio in the background. Idempotent.</summary>
    void Prefetch(MediaId mediaId, string sourceBlobPath);
}

/// <summary>Speech to text with segment timestamps.</summary>
public interface ITranscriptionService
{
    /// <summary>False when no speech model is configured; captions are then not offered.</summary>
    bool IsEnabled { get; }

    Task<IReadOnlyList<TranscriptSegmentDto>> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default);
}

/// <summary>Writes insults about a video and has them spoken, as clips placed on its timeline.</summary>
public interface IVideoRoast
{
    /// <summary>Null when the clip gave nothing to roast.</summary>
    Task<RoastTrack?> GenerateAsync(
        MediaId mediaId, string voice, IReadOnlyList<SceneLabel> labels, IReadOnlyList<TranscriptSegmentDto> transcript,
        double durationSeconds, CancellationToken ct);
}
