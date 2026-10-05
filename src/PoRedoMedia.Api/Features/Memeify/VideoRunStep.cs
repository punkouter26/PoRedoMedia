using PoRedoMedia.Api.Features.Captions;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Memeify;

/// <summary>
/// The three video functions as one step, because they end in one render: Meme-ify adds sounds,
/// stickers and text at the right moments, Insult roast adds a spoken track, Auto-captions burns
/// in subtitles. Whichever are ticked are prepared, then ffmpeg runs once.
/// </summary>
/// <remarks>
/// What is learned about a video (frame labels, loudness, speech) is stored with the source and
/// reused, so a second run on the same video does not pay for vision or transcription again.
/// </remarks>
public sealed class VideoRunStep(
    IServiceProvider services, SourceAudioAnalysis audio, ITranscriptionService transcription, FFmpegRenderService render,
    BlobStorageService blobs, IMediaRepository media, StorageClients storage, IConfiguration configuration,
    IHostEnvironment environment, ILogger<VideoRunStep> logger) : IRunStep
{
    public IReadOnlySet<MediaFunction> Handles { get; } = Available(services, transcription, configuration, environment);

    /// <summary>Only what this server can actually do: no director, no Meme-ify; no speech model, no captions.</summary>
    private static HashSet<MediaFunction> Available(
        IServiceProvider services, ITranscriptionService transcription, IConfiguration configuration, IHostEnvironment environment)
    {
        var available = new HashSet<MediaFunction>();
        if (services.GetService<IDirectorService>() is not null)
            available.Add(MediaFunction.Memeify);
        if (services.GetService<IVideoRoast>() is not null && SessionRoast.Voices(configuration, environment).Count > 0)
            available.Add(MediaFunction.VideoRoast);
        if (transcription.IsEnabled)
            available.Add(MediaFunction.Captions);
        return available;
    }

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var source = context.Source;
        var memeify = context.Has(MediaFunction.Memeify);
        var captions = context.Has(MediaFunction.Captions);
        var persona = context.Option(RunOptions.VideoPersona);

        await context.ReportAsync("Listening to the video");
        var sourceAudio = await audio.GetAsync(source.Id, source.SourcePath, ct);
        var duration = sourceAudio.DurationSeconds > 0 ? sourceAudio.DurationSeconds : source.DurationSeconds ?? 0;
        var transcript = Transcript(context, sourceAudio, captions);

        var labels = memeify || context.Has(MediaFunction.VideoRoast)
            ? [.. (await LabelsAsync(context, ct)).Where(l => l.TimestampSeconds >= 0 && l.TimestampSeconds < duration).OrderBy(l => l.TimestampSeconds)]
            : Array.Empty<SceneLabel>();

        var cues = new List<RenderVisualEntry>();
        string? title = null;
        if (memeify)
            (cues, title) = await DirectAsync(context, labels, transcript, sourceAudio, duration, persona, ct);

        var roast = context.Has(MediaFunction.VideoRoast) ? await RoastAsync(context, labels, transcript, duration, ct) : null;
        if (roast is not null)
            cues.AddRange(roast.Lines.Select(l => new RenderVisualEntry(l.TimestampMs, l.BlobPath, null, null, null, Voice: true)));

        await context.ReportAsync("Rendering");
        var output = new MediaItem
        {
            Owner = source.Owner,
            Id = MediaId.New(),
            Kind = MediaKind.Video,
            Status = MediaStatus.Ready,
            Origin = context.Run.Functions[0].ToString(),
            ParentId = source.Id,
            Title = $"{title ?? string.Join(" + ", context.Run.Functions.Select(FunctionStack.Label))} · {Path.GetFileNameWithoutExtension(source.Title)}",
            ContentType = "video/mp4",
            Extension = ".mp4",
            Text = roast is null ? null : string.Join('\n', roast.Lines.Select(l => l.Text)),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // One ffmpeg run for everything that was ticked.
        var seconds = await render.RenderAsync(new RenderJob(
            MediaId: output.Id,
            SourceBlobPath: source.SourcePath,
            OutputBlobPath: output.SourcePath,
            // The loud personas deep-fry the whole picture; that is a Meme-ify look, not a caption or roast one.
            AggressiveVisuals: memeify && DirectorPrompt.Persona(persona) is "brainrot" or "mlg",
            Cues: cues,
            AspectRatio: context.Option(RunOptions.VideoAspect),
            Subtitles: captions && transcript.Count > 0 ? transcript : null), ct);

        if (captions && transcript.Count > 0)
            await SessionTranscript.SaveAsync(blobs, output.Id, transcript, ct);

        var size = (await storage.Blob(output.SourcePath).GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength;
        output = output with { SizeBytes = size, DurationSeconds = seconds > 0 ? seconds : duration };
        await media.SaveAsync(output, ct);
        await context.AddOutputAsync(output);
    }

    /// <summary>What was said, when speech is available; otherwise nothing, with the reason noted if captions were asked for.</summary>
    private IReadOnlyList<TranscriptSegmentDto> Transcript(RunContext context, SourceAudio sourceAudio, bool captionsWanted)
    {
        var problem = !transcription.IsEnabled ? "no speech model is configured"
            : !sourceAudio.HasAudio ? "the video has no sound"
            : sourceAudio.SpeechError is not null ? "the speech model failed"
            : sourceAudio.Speech.Count == 0 ? "no speech was heard"
            : null;
        if (problem is null)
            return sourceAudio.Speech;

        if (captionsWanted)
            context.AddNote($"No captions were added because {problem}.");
        return [];
    }

    /// <summary>Frame labels stored at upload; else vision on the stored frames; else none, and placement goes by time.</summary>
    private async Task<SceneLabel[]> LabelsAsync(RunContext context, CancellationToken ct)
    {
        var stored = await VisionStore.LoadLabelsAsync(blobs, context.Source.Id, ct);
        if (stored.Length > 0)
            return stored;

        var frames = await VisionStore.LoadFramesAsync(blobs, context.Source.Id, ct);
        if (frames.Count == 0 || services.GetService<IAiVisionService>() is not { } vision)
        {
            context.AddNote("The video's frames were not analysed, so moments were chosen by time rather than by what happens.");
            return [];
        }

        try
        {
            await context.ReportAsync("Watching the video");
            var sounds = (await services.GetRequiredService<ISoundAssetRepository>().LoadAllAsync(ct)).VisibleTo(context.Run.Owner);
            var labels = await vision.AnalyseAsync(frames, SoundVocabulary.Tags(sounds), ct);
            await VisionStore.SaveLabelsAsync(blobs, context.Source.Id, labels, ct);
            return labels;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Frame vision failed for {MediaId}", context.Source.Id);
            context.AddNote("The video could not be analysed, so moments were chosen by time rather than by what happens.");
            return [];
        }
    }

    /// <summary>Picks the moments, asks the director for a cue at each, and resolves every cue's sound.</summary>
    private async Task<(List<RenderVisualEntry> Cues, string? Title)> DirectAsync(
        RunContext context, SceneLabel[] labels, IReadOnlyList<TranscriptSegmentDto> transcript, SourceAudio sourceAudio,
        double duration, string? persona, CancellationToken ct)
    {
        var library = (await services.GetRequiredService<ISoundAssetRepository>().LoadAllAsync(ct)).VisibleTo(context.Run.Owner);
        if (library.Count == 0)
        {
            context.AddNote("The sound library is empty, so no meme sounds were added. Seed it with the seed-sounds command.");
            return ([], null);
        }

        var favorites = await services.GetRequiredService<ISoundFavoritesRepository>().GetAsync(context.Run.Owner, ct);
        var sceneLabels = PlacementPlanner.SceneLabels(labels, transcript, duration);
        var ranked = services.GetRequiredService<ISemanticMatchingService>()
            .GetTopCandidatesBatch(library, [.. sceneLabels.Select(PlacementPlanner.MatchQuery)], topN: 5);
        var plan = PlacementPlanner.Plan(sceneLabels, ranked, library, favorites, duration);

        await context.ReportAsync("Directing");
        ScriptEntry[] entries = [];
        string? title = null;
        try
        {
            var directed = await services.GetRequiredService<IDirectorService>().DirectAsync(
                plan.ApprovedLabels, plan.DirectorMenu, context.Source.Id, labels.Length > 0 || transcript.Count > 0,
                new DirectorContext(persona, transcript, favorites), ct);
            (entries, title) = (directed.Entries, directed.Title);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Director failed for {MediaId}", context.Source.Id);
        }

        if (entries.Length == 0 && plan.Decisions.Count > 0)
        {
            // The director gave nothing usable. The moments and sounds are already chosen, so the
            // video still gets its sounds, without the director's captions, effects and stickers.
            context.AddNote("The AI director was unavailable, so sounds were placed without its captions and effects.");
            entries = [.. plan.Decisions.Select((d, i) => new ScriptEntry
            {
                EntryId = EntryId.New(),
                MediaId = context.Source.Id,
                TimestampMs = d.ApprovedTimestampMs,
                SoundId = d.SelectedSound.SoundId,
                SoundName = d.SelectedSound.DisplayName,
                ActionVectorTags = d.SelectedSound.ActionVectorTags,
                SceneDescription = i < plan.ApprovedLabels.Length ? plan.ApprovedLabels[i].Label : string.Empty,
                PlacementType = d.PlacementType,
            })];
        }

        ApplySubjectDefaults(entries, plan.ApprovedLabels);
        entries = [.. entries.OrderBy(e => e.TimestampMs)];
        SnapToAudio(entries, plan.ApprovedLabels, sourceAudio, duration);

        // A cue whose sound has left the library is dropped rather than failing the render.
        var soundPaths = library.ToDictionary(s => s.SoundId, s => s.BlobPath);
        var cues = entries
            .Where(e => soundPaths.ContainsKey(e.SoundId))
            .Select(e => new RenderVisualEntry(
                e.TimestampMs, soundPaths[e.SoundId], e.VisualEffect?.ToString(), e.CaptionText, e.CaptionPosition,
                FFmpegArgs.ResolveOverlayAssetPath(e.OverlayAssetId), e.OverlayX, e.OverlayY, e.OverlayScale))
            .ToList();
        return (cues, title);
    }

    private async Task<RoastTrack?> RoastAsync(
        RunContext context, SceneLabel[] labels, IReadOnlyList<TranscriptSegmentDto> transcript, double duration, CancellationToken ct)
    {
        var voices = SessionRoast.Voices(configuration, environment);
        var voice = voices.FirstOrDefault(v => v == context.Option(RunOptions.VideoRoastVoice)) ?? voices[0];
        try
        {
            await context.ReportAsync("Writing the roast");
            var track = await services.GetRequiredService<IVideoRoast>().GenerateAsync(context.Source.Id, voice, labels, transcript, duration, ct);
            if (track is null)
                context.AddNote("Nothing in this video gave the roast anything to work with, so it was left out.");
            return track;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The roast is an extra. Losing it must not lose the video.
            logger.LogWarning(ex, "Roast failed for {MediaId}", context.Source.Id);
            context.AddNote("The roast could not be made, so the video was rendered without it.");
            return null;
        }
    }

    /// <summary>A sticker the director did not position goes on the subject seen nearest that moment.</summary>
    internal static void ApplySubjectDefaults(IEnumerable<ScriptEntry> entries, IReadOnlyList<SceneLabel> labels)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.OverlayAssetId) || (entry.OverlayX is not null && entry.OverlayY is not null))
                continue;

            var at = entry.TimestampMs / 1000.0;
            var label = labels.Where(l => l.SubjectX is not null && l.SubjectY is not null)
                .OrderBy(l => Math.Abs(l.TimestampSeconds - at))
                .FirstOrDefault(l => Math.Abs(l.TimestampSeconds - at) < 1.5);
            entry.OverlayX ??= label?.SubjectX;
            entry.OverlayY ??= label?.SubjectY;
        }
    }

    /// <summary>Moves cues onto nearby loud moments in the source's sound. Reactions to speech stay where they are.</summary>
    private static void SnapToAudio(ScriptEntry[] entries, IReadOnlyList<SceneLabel> labels, SourceAudio sourceAudio, double duration)
    {
        if (sourceAudio.Envelope is null || entries.Length == 0)
            return;

        var onsets = CueSnapping.DetectOnsets(sourceAudio.Envelope);
        var labelAt = labels.GroupBy(l => (long)Math.Round(l.TimestampSeconds * 1000)).ToDictionary(g => g.Key, g => g.First());
        var snappable = entries
            .Select(e => !(labelAt.TryGetValue(e.TimestampMs, out var l) && l.Label.StartsWith(PlacementPlanner.SpeechLabelPrefix, StringComparison.Ordinal)))
            .ToArray();
        var snapped = CueSnapping.Snap([.. entries.Select(e => e.TimestampMs)], snappable, onsets, (long)(duration * 1000) - 400);
        for (var i = 0; i < entries.Length; i++)
            entries[i].TimestampMs = snapped[i];
    }
}
