using SkiaSharp;

namespace PoRedoMedia.Api.Features.MemeCaption;

/// <summary>Draws the classic meme: white outlined capitals across the top and bottom of a picture.</summary>
public static class MemeGenerator
{
    public static byte[] Generate(byte[] sourceImage, string? topText, string? bottomText)
    {
        ArgumentNullException.ThrowIfNull(sourceImage);
        if (sourceImage.Length == 0)
            throw new ArgumentException("Image data cannot be empty", nameof(sourceImage));

        using var image = ImageBytes.Decode(sourceImage);
        using (var canvas = new SKCanvas(image))
        {
            if (!string.IsNullOrWhiteSpace(topText))
                Draw(canvas, topText.ToUpperInvariant(), image.Width, image.Height, isTop: true);
            if (!string.IsNullOrWhiteSpace(bottomText))
                Draw(canvas, bottomText.ToUpperInvariant(), image.Width, image.Height, isTop: false);
        }

        return ImageBytes.ToPng(image, zlibLevel: 1);
    }

    private static void Draw(SKCanvas canvas, string text, int width, int height, bool isTop)
    {
        var padding = width * 0.04f;
        MemeTextRenderer.DrawText(
            canvas, text,
            new SKPoint(width / 2f, isTop ? padding : height * 0.65f),
            maxWidth: width - padding * 2f,
            maxFontSize: Math.Min(height / 8f, width / 12f),
            minFontSize: Math.Max(12f, height / 40f),
            SKTextAlign.Center);
    }
}
