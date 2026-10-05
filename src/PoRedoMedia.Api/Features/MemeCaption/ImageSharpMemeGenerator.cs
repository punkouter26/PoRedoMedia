using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PoRedoMedia.Api.Features.MemeCaption;

/// <summary>Draws the classic meme: white outlined capitals across the top and bottom of a picture.</summary>
public static class MemeGenerator
{
    public static byte[] Generate(byte[] sourceImage, string? topText, string? bottomText)
    {
        ArgumentNullException.ThrowIfNull(sourceImage);
        if (sourceImage.Length == 0)
            throw new ArgumentException("Image data cannot be empty", nameof(sourceImage));

        using var image = Image.Load<Rgba32>(sourceImage);
        image.Mutate(ctx =>
        {
            if (!string.IsNullOrWhiteSpace(topText))
                Draw(ctx, topText.ToUpperInvariant(), image.Width, image.Height, isTop: true);
            if (!string.IsNullOrWhiteSpace(bottomText))
                Draw(ctx, bottomText.ToUpperInvariant(), image.Width, image.Height, isTop: false);
        });

        using var png = new MemoryStream();
        image.Save(png, new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed });
        return png.ToArray();
    }

    private static void Draw(IImageProcessingContext ctx, string text, int width, int height, bool isTop)
    {
        var padding = width * 0.04f;
        MemeTextRenderer.DrawText(
            ctx, text,
            new PointF(width / 2f, isTop ? padding : height * 0.65f),
            maxWidth: width - padding * 2f,
            maxFontSize: Math.Min(height / 8f, width / 12f),
            minFontSize: Math.Max(12f, height / 40f),
            HorizontalAlignment.Center);
    }
}
