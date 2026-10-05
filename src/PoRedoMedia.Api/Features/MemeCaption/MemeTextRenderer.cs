using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace PoRedoMedia.Api.Features.MemeCaption;

/// <summary>
/// Shared font-matching and stroke-aware text rendering for meme generation and templates.
/// </summary>
internal static class MemeTextRenderer
{
    private static FontFamily ResolveFontFamily()
    {
        if (SystemFonts.TryGet("Impact", out var fontFamily) ||
            SystemFonts.TryGet("Liberation Sans", out fontFamily) ||
            SystemFonts.TryGet("DejaVu Sans", out fontFamily) ||
            SystemFonts.TryGet("Arial", out fontFamily) ||
            SystemFonts.TryGet("Helvetica", out fontFamily))
        {
            return fontFamily;
        }

        return SystemFonts.Families.First();
    }

    public static void DrawText(
        IImageProcessingContext ctx,
        string text,
        PointF origin,
        float maxWidth,
        float maxFontSize,
        float minFontSize,
        HorizontalAlignment alignment)
    {
        var fontFamily = ResolveFontFamily();
        var fontSize = maxFontSize;
        float strokeWidth = Math.Max(fontSize / 8f, 1.5f);

        // Iteratively shrink the font until the measured (stroke-aware) width fits
        while (fontSize > minFontSize)
        {
            var testFont = fontFamily.CreateFont(fontSize, FontStyle.Bold);
            strokeWidth = Math.Max(fontSize / 8f, 1.5f);
            var probeOptions = new TextOptions(testFont)
            {
                WrappingLength = maxWidth,
                WordBreaking = WordBreaking.BreakWord
            };
            var measured = TextMeasurer.MeasureBounds(text, probeOptions);
            if (measured.Width + (strokeWidth * 2f) <= maxWidth) break;
            fontSize -= Math.Max(2f, fontSize * 0.08f);
        }

        fontSize = Math.Max(fontSize, minFontSize);
        var font = fontFamily.CreateFont(fontSize, FontStyle.Bold);
        strokeWidth = Math.Max(fontSize / 8f, 1.5f);

        var textOptions = new RichTextOptions(font)
        {
            HorizontalAlignment = alignment,
            // Lines of a wrapped caption line up the same way the block does.
            TextAlignment = alignment switch
            {
                HorizontalAlignment.Center => TextAlignment.Center,
                HorizontalAlignment.Right => TextAlignment.End,
                _ => TextAlignment.Start,
            },
            VerticalAlignment = VerticalAlignment.Top,
            Origin = origin,
            WrappingLength = maxWidth,
            WordBreaking = WordBreaking.BreakWord
        };

        // Outline first, then the fill on top. Drawn in one call the outline is painted over the
        // fill and eats into the letters.
        ctx.DrawText(textOptions, text, Pens.Solid(Color.Black, strokeWidth));
        ctx.DrawText(textOptions, text, Brushes.Solid(Color.White));
    }
}

