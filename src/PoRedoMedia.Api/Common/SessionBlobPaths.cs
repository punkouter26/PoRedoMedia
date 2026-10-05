namespace PoRedoMedia.Api.Common;

/// <summary>
/// Blob paths for everything stored under one session. Everything a session owns lives under
/// <see cref="Prefix"/>, which is what lets a single prefix delete (user wipe or retention sweep)
/// remove all of it — keep new per-session artefacts under the same prefix.
/// </summary>
public static class SessionBlobPaths
{
    public static string Prefix(Guid mediaId) => $"{StorageNames.Containers.Media}/{mediaId}/";

    /// <summary>Poster frame for the public share page and the feed.</summary>
    public static string Thumbnail(Guid mediaId) => $"{Prefix(mediaId)}thumb.jpg";

    /// <summary>Speech transcript (JSON array of <c>TranscriptSegmentDto</c>).</summary>
    public static string Transcript(Guid mediaId) => $"{Prefix(mediaId)}transcript.json";

    public static string FramesPrefix(Guid mediaId) => $"{Prefix(mediaId)}frames/";

    public static string Frame(Guid mediaId, int index, string extension) => $"{FramesPrefix(mediaId)}frame_{index:D4}.{extension}";

    /// <summary>Source-time timestamp of each stored keyframe, in frame order (JSON array of seconds).</summary>
    public static string FrameTimestamps(Guid mediaId) => $"{FramesPrefix(mediaId)}timestamps.json";

    /// <summary>Vision labels computed when the frames were uploaded, in source time.</summary>
    public static string VisionLabels(Guid mediaId) => $"{Prefix(mediaId)}vision-labels.json";

    /// <summary>Transcript of the whole source, in source time (the trimmed copy is <see cref="Transcript"/>).</summary>
    public static string SourceSpeech(Guid mediaId) => $"{Prefix(mediaId)}speech-source.json";

    /// <summary>Loudness envelope of the whole source (little-endian float32 RMS, 50 per second).</summary>
    public static string AudioEnvelope(Guid mediaId) => $"{Prefix(mediaId)}audio-envelope.bin";

    /// <summary>The source's length in seconds as ffprobe measured it (invariant-culture text).</summary>
    public static string SourceDuration(Guid mediaId) => $"{Prefix(mediaId)}source-duration.txt";

    /// <summary>The roast: its lines, their times and clips (<c>Common/SessionRoast</c>).</summary>
    public static string Roast(Guid mediaId) => $"{Prefix(mediaId)}roast.json";

    /// <summary>One spoken roast line (or the whole sung track), as the voice engine returned it.</summary>
    public static string RoastClip(Guid mediaId, int index, string extension) => $"{Prefix(mediaId)}roast/line_{index:D2}.{extension}";

    /// <summary>Every roast line back to back: the sound file the Export menu downloads.</summary>
    public static string RoastAudio(Guid mediaId) => $"{Prefix(mediaId)}roast/roast.mp3";

}
