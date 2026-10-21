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
    AiReadStore reads, ILogger<RapRoastStep> logger) : IRunStep
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
        RoastLyrics? lyrics = null;
        var (scene, tags) = await StoredSceneAsync(context, ct);
        if (scene is null)
        {
            var (baseDescription, seenTags, details) = await AnalyseAsync(context, image, ct);
            tags = seenTags;

            // One call reads the picture and writes the first draft. Only when it fails does the
            // roast take the two-call path, which can fall back one stage at a time.
            if (await lyricsWriter.ReadAndWriteAsync(image, tags, details, style, intensity, explicitLanguage, ct) is { } both)
            {
                scene = SceneDescriber.FromSnapshot(both.Scene, details, both.Lyrics.ElapsedMs);
                lyrics = both.Lyrics;
            }
            else
            {
                if (baseDescription is null && await SeeQuietlyAsync(context, image, ct) is { } seen)
                    (baseDescription, tags) = (seen.Description, tags.Count > 0 ? tags : seen.Tags);
                scene = await sceneDescriber.DescribeAsync(image, baseDescription ?? "No description available", tags, details, ct);
            }

            if (scene.Detailed)
                await reads.SaveAsync(context.Current.Id, StoredSceneName, new StoredScene(scene, tags), ct);
        }

        if (scene.FallbackReason is not null)
            context.AddNote(scene.FallbackReason);

        MusicGenerationResult? track = null;
        for (var attempt = 1; attempt <= MaxMusicAttempts; attempt++)
        {
            await context.ReportAsync(attempt == 1 ? "Writing the roast" : "Softening the roast");
            if (attempt > 1 || lyrics is null)
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
            track.ContentType.Contains("wav", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".mp3", ct, text: lyrics.Text,
            detail: scene.Snapshot.HasSubstance ? scene.Snapshot.ToProse() : scene.Text);
        await context.AddOutputAsync(item);
    }

    private const string StoredSceneName = "roast-scene";

    /// <summary>A detailed read of the picture, kept with it so roasting it again only rewrites the bars.</summary>
    private sealed record StoredScene(SceneDescription Scene, IReadOnlyList<string> Tags);

    private async Task<(SceneDescription? Scene, IReadOnlyList<string> Tags)> StoredSceneAsync(RunContext context, CancellationToken ct)
    {
        // A description from the user's device, or a picked vision model, is their call for this run.
        if (context.Option(RunOptions.VisionModel) is not null || context.Option(RunOptions.VisionDescription) is not null)
            return (null, []);

        var stored = await reads.GetAsync<StoredScene>(context.Current.Id, StoredSceneName, ct);
        return stored is { Scene.Detailed: true } ? (stored.Scene, stored.Tags) : (null, []);
    }

    /// <summary>
    /// The hard facts read off the picture, and a description when a provider gives one for free
    /// alongside them. With no picked model, a vision model is not asked here: the roast's own
    /// call looks at the picture, and a second look would be paid for twice.
    /// </summary>
    private async Task<(string? Description, IReadOnlyList<string> Tags, SceneDetails Details)> AnalyseAsync(
        RunContext context, byte[] image, CancellationToken ct)
    {
        var picked = context.Option(RunOptions.VisionModel) is not null || context.Option(RunOptions.VisionDescription) is not null;
        if (!picked && sceneDetails is ICombinedVisionAnalyzer { SupportsCombinedAnalysis: true } combined)
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

        if (!picked)
            return (null, [], await SafeDetailsAsync(image, ct));

        var seeing = visionRouter.SeeAsync(context, image, ct, reads);
        var reading = SafeDetailsAsync(image, ct);
        await Task.WhenAll(seeing, reading);
        return ((await seeing).Description, (await seeing).Tags, await reading);
    }

    /// <summary>A plain description for the two-call path, when nothing gave one yet; null when no provider could.</summary>
    private async Task<VisionResult?> SeeQuietlyAsync(RunContext context, byte[] image, CancellationToken ct)
    {
        try
        {
            return await visionRouter.SeeAsync(context, image, ct, reads);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "No plain description of the picture could be had");
            return null;
        }
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
