namespace PoRedoMedia.Shared.Models;

/// <summary>One spoken line, timed relative to the start of the (trimmed) output.</summary>
public sealed record TranscriptSegmentDto(double StartSeconds, double EndSeconds, string Text);
