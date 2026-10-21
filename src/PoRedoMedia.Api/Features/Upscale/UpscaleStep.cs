using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Upscale;

/// <summary>
/// Redraws the current picture at 2K or 4K. The result replaces the picture later steps work on,
/// so a cutout or a clip made in the same run starts from the sharper one.
/// </summary>
public sealed class UpscaleStep(IImageUpscaler upscaler, BlobStorageService blobs, MediaOutputs outputs) : IRunStep
{
    public IReadOnlySet<MediaFunction> Handles { get; } = new HashSet<MediaFunction> { MediaFunction.Upscale };

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var original = await blobs.ReadAllBytesAsync(context.Current.SourcePath, ct);
        var size = context.Option(RunOptions.UpscaleSize) == "4K" ? "4K" : "2K";

        await context.ReportAsync($"Redrawing the picture at {size}");
        var drawn = await upscaler.UpscaleAsync(ImageBytes.ForProcessing(original), size, ct);

        if (ImageBytes.Identify(original) is { } before && ImageBytes.Identify(drawn.Data) is { } after
            && Math.Max(after.Width, after.Height) <= Math.Max(before.Width, before.Height))
        {
            context.AddNote($"The picture was already {before.Width} x {before.Height}, and the redraw is no larger.");
        }

        var item = await outputs.SaveAsync(
            context.Current, MediaFunction.Upscale, MediaKind.Image, drawn.Data, drawn.ContentType, drawn.Extension, ct, title: $"Upscale {size}");
        await context.AddOutputAsync(item, becomesCurrent: true);
    }
}
