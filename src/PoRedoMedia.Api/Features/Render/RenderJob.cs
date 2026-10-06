using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Render;

/// <summary>One render: the source, where the output goes, and the cues to lay over it.</summary>
public sealed record RenderJob(
    MediaId MediaId,
    string SourceBlobPath,
    string OutputBlobPath,
    bool AggressiveVisuals,
    IReadOnlyList<RenderVisualEntry> Cues,
    string? AspectRatio = null,
    IReadOnlyList<TranscriptSegmentDto>? Subtitles = null);

/// <summary>A cue as the renderer sees it: its sound, and the look around it.</summary>
/// <param name="SoundPath">
/// The sound's blob path in a <see cref="RenderJob"/>; the downloaded file's local path by the
/// time the cue reaches <see cref="FFmpegArgs"/>.
/// </param>
/// <param name="OverlayPath">Local path of the sticker image, when the cue has one that ships.</param>
/// <param name="Voice">A roast line, not a meme sound: louder, and the source audio ducks under it.</param>
public readonly record struct RenderVisualEntry(
    long TimestampMs,
    string SoundPath,
    string? VisualEffect,
    string? CaptionText,
    string? CaptionPosition,
    string? OverlayPath = null,
    double? OverlayX = null,
    double? OverlayY = null,
    double? OverlayScale = null,
    bool Voice = false);
