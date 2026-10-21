using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.RapRoast;

/// <summary>
/// Writes a stand-up roast about the current image and has it performed. The picture is described,
/// then roasted; the lyrics go to the music model, and if it refuses
/// them they are softened once and tried again.
/// </summary>
public sealed class RapRoastStep(
    IVisionService vision, SceneDescriber sceneDescriber,
    RoastLyricsWriter lyricsWriter, IMusicGenerationService music, BlobStorageService blobs, MediaOutputs outputs,
    AiReadStore reads, ILogger<RapRoastStep> logger) : IRunStep
{
    internal const int MaxMusicAttempts = 2;

    /// <summary>The one way a roast is written: as hard as it goes, swearing allowed.</summary>
    private const RoastIntensity Intensity = RoastIntensity.Nuclear;
    private const bool ExplicitLanguage = true;

    public IReadOnlySet<MediaFunction> Handles { get; } = new HashSet<MediaFunction> { MediaFunction.RapRoast };

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var image = ImageBytes.ForProcessing(await blobs.ReadAllBytesAsync(context.Current.SourcePath, ct));

        await context.ReportAsync("Studying the picture");
        RoastLyrics? lyrics = null;
        var (scene, tags) = await StoredSceneAsync(context, ct);
        if (scene is null)
        {
            var (baseDescription, seenTags) = await DeviceDescriptionAsync(context, image, ct);
            tags = seenTags;

            // One call reads the picture and writes the first draft. Only when it fails does the
            // roast take the two-call path, which can fall back one stage at a time.
            if (await lyricsWriter.ReadAndWriteAsync(image, tags, Intensity, ExplicitLanguage, ct) is { } both)
            {
                scene = SceneDescriber.FromSnapshot(both.Scene, both.Lyrics.ElapsedMs);
                lyrics = both.Lyrics;
            }
            else
            {
                if (baseDescription is null && await SeeQuietlyAsync(context, image, ct) is { } seen)
                    (baseDescription, tags) = (seen.Description, tags.Count > 0 ? tags : seen.Tags);
                scene = await sceneDescriber.DescribeAsync(image, baseDescription ?? "No description available", tags, ct);
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
                lyrics = await lyricsWriter.WriteAsync(scene.Text, tags, Intensity, softened: attempt > 1, ExplicitLanguage, ct);

            await context.ReportAsync("Performing it");
            track = await music.GenerateAsync(lyrics.Text, StandUpPrompt, ct);
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
        // A description from the user's device is their call for this run.
        if (context.Option(RunOptions.VisionDescription) is not null)
            return (null, []);

        var stored = await reads.GetAsync<StoredScene>(context.Current.Id, StoredSceneName, ct);
        return stored is { Scene.Detailed: true } ? (stored.Scene, stored.Tags) : (null, []);
    }

    /// <summary>
    /// The description the device wrote, when it wrote one. Otherwise a vision model is not asked
    /// here: the roast's own call looks at the picture, and a second look would be paid for twice.
    /// </summary>
    private async Task<(string? Description, IReadOnlyList<string> Tags)> DeviceDescriptionAsync(
        RunContext context, byte[] image, CancellationToken ct)
    {
        if (context.Option(RunOptions.VisionDescription) is null)
            return (null, []);

        var seen = await vision.SeeAsync(context, image, ct, reads);
        return (seen.Description, seen.Tags);
    }

    /// <summary>A plain description for the two-call path, when nothing gave one yet; null when no provider could.</summary>
    private async Task<VisionResult?> SeeQuietlyAsync(RunContext context, byte[] image, CancellationToken ct)
    {
        try
        {
            return await vision.SeeAsync(context, image, ct, reads);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "No plain description of the picture could be had");
            return null;
        }
    }

    private const string StandUpPrompt =
        "A live stand-up comedy club recording. SPOKEN delivery, not sung and not rapped: a "
        + "comedian working a microphone, dry and conversational, with comic timing and pauses "
        + "for laughs. Small room tone, scattered crowd laughter and reactions. Little or no "
        + "musical backing; if any, a barely-there jazz brush loop under the voice.";
}
