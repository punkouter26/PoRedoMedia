using Microsoft.Extensions.AI;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Restyle;

/// <summary>
/// Redraws the current image. The image model never sees the picture: a chat model that can see
/// writes a detailed description, the chosen style is added to it, and a new image is generated
/// from those words in the original's shape.
/// </summary>
public sealed class RestyleStep(
    ReproductionPromptWriter writer, IVisionServiceRouter vision, IImageGenerationService images,
    BlobStorageService blobs, MediaOutputs outputs) : IRunStep
{
    public IReadOnlySet<MediaFunction> Handles { get; } = new HashSet<MediaFunction> { MediaFunction.Restyle };

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var image = ImageBytes.ForProcessing(await blobs.ReadAllBytesAsync(context.Current.SourcePath, ct));

        await context.ReportAsync("Studying the picture");
        var prompt = await writer.TryWriteAsync(image, ct);
        if (prompt is null)
        {
            // The detailed read failed. A plain description still gives a usable picture, but the
            // user must know the result came from less.
            var seen = await vision.Resolve(context.Option(RunOptions.VisionModel)).AnalyzeAsync(image, ct);
            prompt = $"{seen.Description}. Elements: {string.Join(", ", seen.Tags)}";
            context.AddNote("The detailed read of your picture failed, so the new image was drawn from a short description instead.");
        }

        var style = context.Option(RunOptions.RestylePrompt)
            ?? StyleRecipeCatalog.All.FirstOrDefault(r => r.Id == context.Option(RunOptions.RestyleStyle))?.PromptSnippet;
        if (style is not null)
            prompt = $"{prompt}\n\nRender the whole scene in this style: {style}";

        await context.ReportAsync("Drawing the new picture");
        var drawn = await images.GenerateAsync(prompt, matchAspectOf: image, ct);

        var item = await outputs.SaveAsync(context.Current, MediaFunction.Restyle, MediaKind.Image, drawn.Data, drawn.ContentType, drawn.Extension, ct);
        await context.AddOutputAsync(item, becomesCurrent: true);
    }
}

/// <summary>Has a chat model that can see write the words a text-to-image model needs to redraw a photograph.</summary>
public sealed class ReproductionPromptWriter(IChatClient chat, ILogger<ReproductionPromptWriter> logger)
{
    private const string SystemPrompt =
        "You write prompts for a text-to-image model that must REPRODUCE a photograph it will never "
        + "see. Your prompt is the only thing it receives, so every visual fact you omit is a fact it "
        + "will invent.\n"
        + "Reply with the prompt itself and nothing else: no preamble, no explanation, no markdown, "
        + "no headings, no numbering.\n"
        + "Write comma-separated visual clauses, not sentences, in this order:\n"
        + "1. medium and shot type (photograph, film still, illustration; candid, portrait, close-up, wide shot)\n"
        + "2. the main subject: how many people, what each is doing, their pose, and where they are looking\n"
        + "3. what you would need to redraw them: hair colour, length and style, facial hair, eyewear, "
        + "headwear, and facial expression\n"
        + "4. clothing, garment by garment, with colour, material and fit\n"
        + "5. every significant object, with colour, material, state, and position relative to the subject\n"
        + "6. the setting and background, near to far\n"
        + "7. lighting: direction, hardness, colour temperature, and where highlights and shadows fall\n"
        + "8. the dominant colour palette, named concretely\n"
        + "9. camera angle, height, distance, lens character and depth of field\n"
        + "10. framing: what sits left, centre and right, and what the frame edges cut off\n"
        + "Rules. Describe only what is visible; never invent, never guess at what is out of frame, "
        + "never add a style the photograph does not already have. Be concrete: \"round gold "
        + "wire-frame glasses\" beats \"glasses\", \"mustard yellow\" beats \"yellowish\". Do not "
        + "name or infer race, ethnicity, skin tone, age, body size, or disability. Do not "
        + "editorialise, do not interpret mood, do not tell a story.";

    /// <summary>The prompt, or null when the model failed or said nothing. The caller falls back and tells the user.</summary>
    public async Task<string?> TryWriteAsync(byte[] image, CancellationToken ct)
    {
        try
        {
            var response = await chat.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User,
                [
                    new DataContent(image, "image/jpeg"),
                    new TextContent("Use at most 300 words. Spend them on visual facts, not on grammar.\n\nStudy the image and write the reproduction prompt."),
                ]),
            ], cancellationToken: ct);
            return string.IsNullOrWhiteSpace(response.Text) ? null : response.Text.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reproduction prompt failed");
            return null;
        }
    }
}
