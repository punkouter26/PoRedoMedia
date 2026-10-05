using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.RapRoast;

/// <summary>
/// Writes a roast about the current image and has it performed. The picture is read for hard
/// facts first, then described, then roasted; the lyrics go to the music model, and if it refuses
/// them they are softened once and tried again.
/// </summary>
public sealed class RapRoastStep(
    IVisionServiceRouter visionRouter, ISceneDetailProvider sceneDetails, SceneDescriber sceneDescriber,
    RoastLyricsWriter lyricsWriter, IMusicGenerationService music, BlobStorageService blobs, MediaOutputs outputs,
    ILogger<RapRoastStep> logger) : IRunStep
{
    internal const int MaxMusicAttempts = 2;

    public IReadOnlySet<MediaFunction> Handles { get; } = new HashSet<MediaFunction> { MediaFunction.RapRoast };

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var image = ImageBytes.ForProcessing(await blobs.ReadAllBytesAsync(context.Current.SourcePath, ct));
        var style = Enum.TryParse<RapStyle>(context.Option(RunOptions.RoastStyle), out var s) ? s : RapStyle.Trap;
        var intensity = Enum.TryParse<RoastIntensity>(context.Option(RunOptions.RoastIntensity), out var i) ? i : RoastIntensity.Roast;
        var explicitLanguage = context.Option(RunOptions.RoastExplicit) == "true";

        await context.ReportAsync("Studying the picture");
        var (baseDescription, tags, details) = await AnalyseAsync(context, image, ct);
        var scene = await sceneDescriber.DescribeAsync(image, baseDescription, tags, details, ct);
        if (scene.FallbackReason is not null)
            context.AddNote(scene.FallbackReason);

        RoastLyrics? lyrics = null;
        MusicGenerationResult? track = null;
        for (var attempt = 1; attempt <= MaxMusicAttempts; attempt++)
        {
            await context.ReportAsync(attempt == 1 ? "Writing the roast" : "Softening the roast");
            lyrics = await lyricsWriter.WriteAsync(scene.Text, tags, style, intensity, softened: attempt > 1, explicitLanguage, ct);

            await context.ReportAsync("Performing it");
            track = await music.GenerateAsync(lyrics.Text, StylePrompt(style), ct);
            if (!track.Refused)
                break;
            logger.LogInformation("Music provider refused attempt {Attempt}: {Reason}", attempt, track.RefusalReason);
        }

        if (lyrics!.FallbackReason is not null)
            context.AddNote(lyrics.FallbackReason);

        if (track!.Refused)
        {
            // Not a failure of the run: the words exist, only the performance was declined.
            context.AddNote("The music provider declined to perform the roast, so there is no audio. The lyrics were: " + lyrics.Text);
            return;
        }

        if (lyrics.Softened)
            context.AddNote("The first draft was refused by the music provider, so the roast was softened.");

        var item = await outputs.SaveAsync(
            context.Current, MediaFunction.RapRoast, MediaKind.Audio, track.Audio, track.ContentType,
            track.ContentType.Contains("wav", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".mp3", ct, text: lyrics.Text);
        await context.AddOutputAsync(item);
    }

    /// <summary>One combined call when the detail provider can do it; otherwise vision and detail side by side.</summary>
    private async Task<(string Description, IReadOnlyList<string> Tags, SceneDetails Details)> AnalyseAsync(
        RunContext context, byte[] image, CancellationToken ct)
    {
        if (context.Option(RunOptions.VisionModel) is null && context.Option(RunOptions.VisionDescription) is null && sceneDetails is ICombinedVisionAnalyzer { SupportsCombinedAnalysis: true } combined)
        {
            try
            {
                var all = await combined.AnalyzeAllAsync(image, ct);
                return (all.Description, all.Tags, all.Details);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Combined vision analysis failed; using separate calls");
            }
        }

        var seeing = visionRouter.SeeAsync(context, image, ct);
        var reading = SafeDetailsAsync(image, ct);
        await Task.WhenAll(seeing, reading);
        return ((await seeing).Description, (await seeing).Tags, await reading);
    }

    private async Task<SceneDetails> SafeDetailsAsync(byte[] image, CancellationToken ct)
    {
        try
        {
            return await sceneDetails.GetDetailsAsync(image, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Scene detail extraction failed; continuing without it");
            return SceneDetails.Empty;
        }
    }

    private static string StylePrompt(RapStyle style) => style switch
    {
        RapStyle.StandUp =>
            "A live stand-up comedy club recording. SPOKEN delivery, not sung and not rapped: a "
            + "comedian working a microphone, dry and conversational, with comic timing and pauses "
            + "for laughs. Small room tone, scattered crowd laughter and reactions. Little or no "
            + "musical backing; if any, a barely-there jazz brush loop under the voice.",
        RapStyle.Trap =>
            "A modern trap rap track. Booming 808 sub-bass, rapid hi-hat rolls, half-time feel around "
            + "140 BPM. Confident male rap vocal, clear diction, punchy delivery.",
        _ =>
            "An old-school 1980s party rap track. Live funk drum break, horn stabs, electric bass, "
            + "around 105 BPM. Energetic playful rap vocal with crowd energy.",
    };
}
