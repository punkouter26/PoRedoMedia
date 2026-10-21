using SkiaSharp;

namespace PoRedoMedia.Api.Common;

/// <summary>Decoding, turning, scaling and encoding of still images, for AI providers, thumbnails and upload checks.</summary>
public static class ImageBytes
{
    /// <summary>
    /// Standard YCbCr 4:2:0, baseline, so all three components travel in ONE interleaved scan. A
    /// JPEG written one scan per component is read back correctly by some decoders but not others:
    /// Azure OpenAI described a puppy photo encoded that way as "a green-tinted silhouette of a
    /// person" (seen 2026-10-05).
    /// </summary>
    private static readonly SKJpegEncoderOptions Jpeg = new(90, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore);

    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>Longest edge sent to an AI provider or drawn on. Larger uploads are scaled down first.</summary>
    public const int MaxEdge = 1568;

    /// <summary>
    /// The image as a JPEG no larger than <see cref="MaxEdge"/> on its longest side. Providers cap
    /// request size, and nothing here needs more pixels than that.
    /// </summary>
    public static byte[] ForProcessing(byte[] image) => ToJpeg(image, MaxEdge);

    /// <summary>
    /// The first frame, turned the way its EXIF tag says, as a JPEG no larger than
    /// <paramref name="maxEdge"/> on its longest side. A phone photo is stored on its side with a
    /// tag saying how to turn it; turned here, text is drawn on, and the AI is shown, the picture
    /// the way the user sees it.
    /// </summary>
    public static byte[] ToJpeg(byte[] image, int maxEdge)
    {
        using var codec = OpenCodec(image) ?? throw Unreadable();
        var stored = codec.Info.Size;
        var fit = Math.Min(1f, (float)maxEdge / Math.Max(stored.Width, stored.Height));

        // JPEG decodes straight to a fraction (n/8) of full size, so a 40 MP photo is never held
        // whole. Skia rounds a requested scale to the nearest fraction, so ask for the next one up
        // and fall back to full size rather than ever stretching a too-small decode.
        var near = codec.GetScaledDimensions(Math.Min(8, MathF.Ceiling(fit * 8)) / 8f);
        if (Math.Max(near.Width, near.Height) < Math.Round(Math.Max(stored.Width, stored.Height) * fit))
            near = stored;
        using var decoded = SKBitmap.Decode(codec, codec.Info.WithSize(near.Width, near.Height)) ?? throw Unreadable();

        var origin = codec.EncodedOrigin;
        var sideways = origin >= SKEncodedOrigin.LeftTop; // LeftTop..LeftBottom: stored a quarter turn off
        var width = Math.Max(1, (int)Math.Round((sideways ? stored.Height : stored.Width) * fit));
        var height = Math.Max(1, (int)Math.Round((sideways ? stored.Width : stored.Height) * fit));

        // One draw turns and scales together, and converts a wide-gamut photo (Display P3, Adobe
        // RGB) to the sRGB an untagged JPEG is read as. JPEG has no transparency: it shows on white.
        using var shown = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using (var canvas = new SKCanvas(shown))
        {
            canvas.Clear(SKColors.White);
            var scale = SKMatrix.CreateScale((float)width / (sideways ? decoded.Height : decoded.Width), (float)height / (sideways ? decoded.Width : decoded.Height));
            canvas.SetMatrix(SKMatrix.Concat(scale, Turn(origin, decoded.Width, decoded.Height)));
            canvas.DrawBitmap(decoded, 0, 0, Smooth, null);
        }

        return Encode(shown, pixels => pixels.Encode(Jpeg));
    }

    /// <summary>The width and height an image's header declares, or null when it is not a readable image. Decodes no pixels.</summary>
    public static SKSizeI? Identify(byte[] image)
    {
        using var codec = OpenCodec(image);
        return codec?.Info.Size;
    }

    /// <summary>The first frame of the image, as stored. Throws when it is not a readable image.</summary>
    public static SKBitmap Decode(byte[] image) => SKBitmap.Decode(image) ?? throw Unreadable();

    public static byte[] ToPng(SKBitmap bitmap, int zlibLevel = 6) =>
        Encode(bitmap, pixels => pixels.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, zlibLevel)));

    // Read through a stream rather than copied into native memory, so a header check touches only the header.
    private static SKCodec? OpenCodec(byte[] image) => SKCodec.Create(new MemoryStream(image, writable: false));

    private static ArgumentException Unreadable() => new("That is not a readable image.", "image");

    /// <summary>
    /// Maps a stored pixel to where it shows, for a <paramref name="w"/> x <paramref name="h"/> image:
    /// x' = ScaleX*x + SkewX*y + TransX, y' = SkewY*x + ScaleY*y + TransY.
    /// </summary>
    private static SKMatrix Turn(SKEncodedOrigin origin, float w, float h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private static byte[] Encode(SKBitmap bitmap, Func<SKPixmap, SKData?> encode)
    {
        using var pixels = bitmap.PeekPixels() ?? throw new InvalidOperationException("The image has no pixels to encode.");
        using var data = encode(pixels) ?? throw new InvalidOperationException("The image could not be encoded.");
        return data.ToArray();
    }
}
