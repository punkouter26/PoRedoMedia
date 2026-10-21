using SkiaSharp;

namespace PoRedoMedia.Api.Features.MemeCaption;

/// <summary>
/// Shared font-matching and stroke-aware text rendering for meme generation and templates.
/// </summary>
internal static class MemeTextRenderer
{
    private static readonly Lazy<SKTypeface> Typeface = new(ResolveTypeface);

    private static SKTypeface ResolveTypeface()
    {
        // Linux uses Skia's no-dependencies build, which has no fontconfig, so family lookups come
        // back empty there and the font file is used instead.
        foreach (var family in new[] { "Impact", "Liberation Sans", "DejaVu Sans", "Arial", "Helvetica" })
        {
            if (SKFontManager.Default.MatchFamily(family, SKFontStyle.Bold) is { } typeface)
                return typeface;
        }

        foreach (var file in new[] { FFmpegProcess.BundledFontPath, FFmpegProcess.SystemFontPath })
        {
            if (File.Exists(file) && SKTypeface.FromFile(file) is { } typeface)
                return typeface;
        }

        // On the no-dependencies build the default typeface has no glyphs: a meme drawn with it
        // would silently carry no caption, so fail the step instead (a run with no output is refunded).
        return SKTypeface.Default is { GlyphCount: > 0 } fallback
            ? fallback
            : throw new InvalidOperationException($"No font to draw captions with; expected {FFmpegProcess.BundledFontPath}.");
    }

    public static void DrawText(
        SKCanvas canvas,
        string text,
        SKPoint origin,
        float maxWidth,
        float maxFontSize,
        float minFontSize,
        SKTextAlign alignment)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        using var font = new SKFont(Typeface.Value, Math.Max(maxFontSize, minFontSize));
        float strokeWidth;
        List<string> lines;

        // Iteratively shrink the font until the widest wrapped line, outline included, fits
        while (true)
        {
            strokeWidth = Math.Max(font.Size / 8f, 1.5f);
            (lines, var widest) = Wrap(text, font, maxWidth);
            if (font.Size <= minFontSize || widest + (strokeWidth * 2f) <= maxWidth) break;
            font.Size = Math.Max(minFontSize, font.Size - Math.Max(2f, font.Size * 0.08f));
        }

        using var outline = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            StrokeJoin = SKStrokeJoin.Round,
        };
        using var fill = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };

        // The block hangs from the origin; each line lines up the same way the block does.
        var baseline = origin.Y - font.Metrics.Ascent;
        foreach (var line in lines)
        {
            // Outline first, then the fill on top. Drawn the other way round the outline is
            // painted over the fill and eats into the letters.
            canvas.DrawText(line, origin.X, baseline, alignment, font, outline);
            canvas.DrawText(line, origin.X, baseline, alignment, font, fill);
            baseline += font.Spacing;
        }
    }

    /// <summary>
    /// Greedy word wrap at <paramref name="maxWidth"/>, and the width of the widest line. A line
    /// break in the text always starts a new line; a word wider than a line on its own is broken
    /// between letters.
    /// </summary>
    private static (List<string> Lines, float Widest) Wrap(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        var widest = 0f;
        var line = new System.Text.StringBuilder();
        var lineWidth = 0f;
        var space = font.MeasureText(" ");

        void EndLine()
        {
            if (line.Length == 0) return;
            lines.Add(line.ToString());
            widest = Math.Max(widest, lineWidth);
            line.Clear();
            lineWidth = 0;
        }

        foreach (var paragraph in text.Split('\n'))
        {
            foreach (var word in paragraph.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var wordWidth = font.MeasureText(word);
                if (line.Length > 0 && lineWidth + space + wordWidth <= maxWidth)
                {
                    line.Append(' ').Append(word);
                    lineWidth += space + wordWidth;
                    continue;
                }

                EndLine();
                var rest = word.AsSpan();
                while (font.MeasureText(rest) > maxWidth)
                {
                    var fits = Math.Max(1, font.BreakText(rest, maxWidth));
                    line.Append(rest[..fits]);
                    lineWidth = font.MeasureText(rest[..fits]);
                    EndLine();
                    rest = rest[fits..];
                }

                line.Append(rest);
                lineWidth = font.MeasureText(rest);
            }

            EndLine();
        }

        return (lines, widest);
    }
}
