using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.MemeCaption;

/// <summary>
/// Puts meme text on the current image. The text comes from the AI (it looks at the picture,
/// then writes a caption), from the user, or from the user through one of the template layouts.
/// </summary>
public sealed class MemeCaptionStep(
    IVisionServiceRouter vision, ICaptionWriter writer, MemeTemplateService templates,
    BlobStorageService blobs, MediaOutputs outputs) : IRunStep
{
    public IReadOnlySet<MediaFunction> Handles { get; } = new HashSet<MediaFunction> { MediaFunction.MemeCaption };

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var image = ImageBytes.ForProcessing(await blobs.ReadAllBytesAsync(context.Current.SourcePath, ct));

        byte[] meme;
        switch (context.Option(RunOptions.MemeMode))
        {
            case RunOptions.MemeModeTemplate:
                var template = templates.GetById(context.Option(RunOptions.MemeTemplate) ?? "")
                    ?? throw new RunStepException("Pick a meme template.");
                var lines = Enumerable.Range(0, template.Zones.Count).Select(i => context.Option(RunOptions.MemeZone(i)) ?? "").ToList();
                if (lines.Take(template.RequiredZoneCount).Any(string.IsNullOrWhiteSpace))
                    throw new RunStepException($"{template.Name} needs {template.RequiredZoneCount} lines of text.");
                meme = (await templates.RenderAsync(image, template, lines, ct)).ImageData;
                break;

            case RunOptions.MemeModeText:
                var (top, bottom) = (context.Option(RunOptions.MemeTop), context.Option(RunOptions.MemeBottom));
                if (top is null && bottom is null)
                    throw new RunStepException("Type the top or the bottom text.");
                meme = MemeGenerator.Generate(image, top, bottom);
                break;

            default:
                await context.ReportAsync("Looking at the picture");
                var seen = await vision.SeeAsync(context, image, ct);
                if (seen.FallbackReason is not null)
                    context.AddNote(seen.FallbackReason);
                await context.ReportAsync("Writing the caption");
                var caption = await writer.WriteAsync(seen.Description, seen.Tags, ct);
                meme = MemeGenerator.Generate(image, caption.Top, caption.Bottom);
                break;
        }

        var item = await outputs.SaveAsync(context.Current, MediaFunction.MemeCaption, MediaKind.Image, meme, "image/png", ".png", ct);
        await context.AddOutputAsync(item, becomesCurrent: true);
    }
}

public static class MemeTemplateEndpoints
{
    public static IEndpointRouteBuilder MapMemeTemplates(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/meme-templates", (MemeTemplateService templates) => TypedResults.Ok(templates.GetTemplates()
            .Select(t => new MemeTemplateDto(t.Id, t.Name, t.Description, t.RequiredZoneCount, [.. t.Zones.Select(z => z.Label)]))
            .ToList()));
        return app;
    }
}
