using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using PoRedoMedia.Mobile.Models;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using ImageSharpResizeMode = SixLabors.ImageSharp.Processing.ResizeMode;
using ImageSharpSize = SixLabors.ImageSharp.Size;

namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Downscales large camera photos to mobile-optimized AI dimensions and fixes EXIF orientation.
/// </summary>
public class ImageOptimizationService : IImageOptimizationService
{
    public async Task<ImageCaptureResult> OptimizeAsync(
        Stream imageStream,
        string fileName,
        string contentType,
        int maxDimension = 1280,
        int quality = 85,
        CancellationToken ct = default)
    {
        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream, ct);
        var bytes = memoryStream.ToArray();

        return await OptimizeAsync(bytes, fileName, contentType, maxDimension, quality, ct);
    }

    public async Task<ImageCaptureResult> OptimizeAsync(
        byte[] rawBytes,
        string fileName,
        string contentType,
        int maxDimension = 1280,
        int quality = 85,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var image = ImageSharpImage.Load(rawBytes);

                // Automatically fix orientation based on camera EXIF tags (e.g. portrait photos)
                image.Mutate(x => x.AutoOrient());

                // Resize down if larger than maxDimension while preserving aspect ratio
                if (image.Width > maxDimension || image.Height > maxDimension)
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new ImageSharpSize(maxDimension, maxDimension),
                        Mode = ImageSharpResizeMode.Max
                    }));
                }

                using var outStream = new MemoryStream();
                var encoder = new JpegEncoder
                {
                    Quality = quality
                };
                image.Save(outStream, encoder);
                var optimizedBytes = outStream.ToArray();
                var optimizedBase64 = Convert.ToBase64String(optimizedBytes);

                return new ImageCaptureResult(
                    Path.ChangeExtension(fileName, ".jpg"),
                    "image/jpeg",
                    optimizedBytes,
                    optimizedBase64,
                    optimizedBytes.Length,
                    image.Width,
                    image.Height);
            }
            catch
            {
                // Resilient fallback to raw bytes if decoding fails
                var base64 = Convert.ToBase64String(rawBytes);
                return new ImageCaptureResult(
                    fileName,
                    contentType,
                    rawBytes,
                    base64,
                    rawBytes.Length);
            }
        }, ct);
    }
}
