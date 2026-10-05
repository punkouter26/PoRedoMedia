using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace PoRedoMedia.Api.Common.Ai;

public static class ImageBytes
{
    /// <summary>Longest edge sent to an AI provider or drawn on. Larger uploads are scaled down first.</summary>
    public const int MaxEdge = 1568;

    /// <summary>
    /// The image as a JPEG no larger than <see cref="MaxEdge"/> on its longest side. Providers cap
    /// request size, and nothing here needs more pixels than that.
    /// </summary>
    public static byte[] ForProcessing(byte[] image)
    {
        using var loaded = Image.Load(image);
        if (Math.Max(loaded.Width, loaded.Height) > MaxEdge)
            loaded.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(MaxEdge, MaxEdge), Mode = ResizeMode.Max }));

        using var jpeg = new MemoryStream();
        loaded.Save(jpeg, new JpegEncoder { Quality = 90 });
        return jpeg.ToArray();
    }
}
