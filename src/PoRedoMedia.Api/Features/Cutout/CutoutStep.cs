using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;
using SkiaSharp;

namespace PoRedoMedia.Api.Features.Cutout;

/// <summary>
/// Lifts the subject of the current picture off its background, as a transparent PNG, and by
/// default also makes a sticker of it. The picture later steps work on is not replaced.
/// </summary>
public sealed class CutoutStep(IBackgroundRemover remover, BlobStorageService blobs, MediaOutputs outputs) : IRunStep
{
    /// <summary>Longest edge sent to the remover. Enough for a sharp cutout; larger is slower and no better.</summary>
    private const int MaxEdge = 2048;

    public IReadOnlySet<MediaFunction> Handles { get; } = new HashSet<MediaFunction> { MediaFunction.Cutout };

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var image = ImageBytes.ToJpeg(await blobs.ReadAllBytesAsync(context.Current.SourcePath, ct), MaxEdge);

        await context.ReportAsync("Lifting the subject off its background");
        var cut = await remover.RemoveAsync(image, "image/jpeg", ct);
        if (!Sticker.HasSubject(cut))
            throw new RunStepException("No subject was found to cut out. A clear subject in front of its background works best.");

        var item = await outputs.SaveAsync(context.Current, MediaFunction.Cutout, MediaKind.Image, cut, "image/png", ".png", ct);
        await context.AddOutputAsync(item);

        if (context.Option(RunOptions.CutoutSticker) == "false")
            return;

        await context.ReportAsync("Making the sticker");
        var sticker = await outputs.SaveAsync(
            context.Current, MediaFunction.Cutout, MediaKind.Image, Sticker.Make(cut), "image/webp", ".webp", ct, title: "Sticker");
        await context.AddOutputAsync(sticker);
    }
}

/// <summary>
/// Chat-app stickers: 512 x 512 WebP, transparent around a subject with a white outline, small
/// enough for WhatsApp and Telegram packs.
/// </summary>
public static class Sticker
{
    public const int Size = 512;
    private const int Outline = 10;
    private const int Margin = Outline + 6;

    /// <summary>Alpha at or below this counts as background.</summary>
    private const byte Clear = 16;

    /// <summary>True when at least half a percent of the picture is not background.</summary>
    public static bool HasSubject(byte[] png)
    {
        using var bitmap = ImageBytes.Decode(png);
        return bitmap.Pixels.Count(p => p.Alpha > Clear) > 0.005 * bitmap.Width * bitmap.Height;
    }

    public static byte[] Make(byte[] png)
    {
        using var cut = ImageBytes.Decode(png);
        var bounds = Bounds(cut) ?? new SKRectI(0, 0, cut.Width, cut.Height);
        var room = Size - 2 * Margin;
        var scale = Math.Min((float)room / bounds.Width, (float)room / bounds.Height);
        var (width, height) = (bounds.Width * scale, bounds.Height * scale);
        var target = SKRect.Create((Size - width) / 2, (Size - height) / 2, width, height);

        using var sticker = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(sticker))
        {
            canvas.Clear(SKColors.Transparent);
            var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
            using var image = SKImage.FromBitmap(cut);

            // The outline: the subject's shape in white, stamped in a ring around where it goes.
            using var white = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn) };
            for (var step = 0; step < 32; step++)
            {
                var angle = step * MathF.PI / 16;
                var ring = target;
                ring.Offset(Outline * MathF.Cos(angle), Outline * MathF.Sin(angle));
                canvas.DrawImage(image, bounds, ring, sampling, white);
            }

            canvas.DrawImage(image, bounds, target, sampling, null);
        }

        using var pixels = sticker.PeekPixels();
        using var webp = pixels.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 90))
            ?? throw new InvalidOperationException("The sticker could not be encoded.");
        return webp.ToArray();
    }

    /// <summary>The smallest rectangle holding everything that is not background, or null when nothing is.</summary>
    private static SKRectI? Bounds(SKBitmap bitmap)
    {
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        var pixels = bitmap.Pixels;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (pixels[y * bitmap.Width + x].Alpha <= Clear)
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < 0 ? null : new SKRectI(left, top, right + 1, bottom + 1);
    }
}
