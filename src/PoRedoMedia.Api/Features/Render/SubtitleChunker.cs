// SOLID: Single Responsibility — pure subtitle line-fitting, unit-tested in isolation
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Render;

/// <summary>
/// Splits transcript segments into on-screen subtitle cards short enough for one line.
/// </summary>
/// <remarks>
/// ffmpeg's drawtext does not wrap, so a 20-word Whisper segment would run off both edges of a
/// 720p frame. Each segment is cut at word boundaries into cards of at most
/// <see cref="MaxCharsPerCard"/> characters, and the segment's time is divided between them in
/// proportion to their length — close enough to speech pace that the text tracks the voice.
/// </remarks>
internal static class SubtitleChunker
{
    /// <summary>Fits a 720px-wide frame at the subtitle font size with margin to spare.</summary>
    public const int MaxCharsPerCard = 40;

    public static IReadOnlyList<TranscriptSegmentDto> Chunk(IEnumerable<TranscriptSegmentDto> segments, int maxChars = MaxCharsPerCard)
    {
        var cards = new List<TranscriptSegmentDto>();
        foreach (var seg in segments)
        {
            var text = seg.Text.Trim();
            if (text.Length == 0 || seg.EndSeconds <= seg.StartSeconds)
                continue;

            var lines = SplitWords(text, maxChars);
            var totalChars = lines.Sum(l => l.Length);
            var duration = seg.EndSeconds - seg.StartSeconds;
            var cursor = seg.StartSeconds;

            foreach (var line in lines)
            {
                var share = duration * line.Length / totalChars;
                cards.Add(new TranscriptSegmentDto(cursor, cursor + share, line));
                cursor += share;
            }
        }

        return cards;
    }

    private static List<string> SplitWords(string text, int maxChars)
    {
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > maxChars)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
                current.Append(' ');
            current.Append(word);
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        return lines;
    }
}
