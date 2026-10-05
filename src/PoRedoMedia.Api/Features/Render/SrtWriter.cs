using System.Globalization;
using System.Text;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Render;

/// <summary>Formats a transcript as SubRip (.srt) — the subtitle format every editor and platform accepts.</summary>
internal static class SrtWriter
{
    public static string Write(IEnumerable<TranscriptSegmentDto> segments)
    {
        var sb = new StringBuilder();
        var index = 1;
        foreach (var seg in segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)).OrderBy(s => s.StartSeconds))
        {
            sb.Append(index++).Append("\r\n")
              .Append(Timestamp(seg.StartSeconds)).Append(" --> ").Append(Timestamp(seg.EndSeconds)).Append("\r\n")
              .Append(seg.Text.Trim()).Append("\r\n\r\n");
        }

        return sb.ToString();
    }

    /// <summary>SRT timestamps are <c>HH:MM:SS,mmm</c> — a comma, not a dot, before the milliseconds.</summary>
    internal static string Timestamp(double seconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
    }
}
