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
            VerticalAlignment = VerticalAlignment.Top,
            Origin = origin,
            WrappingLength = maxWidth,
            WordBreaking = WordBreaking.BreakWord
        };

        ctx.DrawText(textOptions, text, Brushes.Solid(Color.White), Pens.Solid(Color.Black, strokeWidth));
    }
}

