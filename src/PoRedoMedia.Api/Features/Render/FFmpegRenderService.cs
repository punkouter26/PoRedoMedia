
namespace PoRedoMedia.Api.Features.Render;

/// <summary>
/// The ffmpeg work: the render, the GIF export, the poster frame and the source-audio decode.
/// Command lines come from <see cref="FFmpegArgs"/>; <see cref="FFmpegProcess"/> runs them.
/// </summary>
/// <remarks>
/// Renders used to go through a bounded channel with a single consumer of its own. Its only
/// caller is the engine dispatcher, which already runs one session at a time, so the second queue
/// ordered nothing — it only moved the render onto a token the caller could not cancel.
/// </remarks>
public sealed partial class FFmpegRenderService : IMediaToolkit
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Thumbnail extraction failed for session {MediaId}; the share page will fall back to a keyframe")]
    private partial void LogThumbnailFailed(Exception ex, MediaId mediaId);

    /// <summary>
    /// One ad-hoc ffmpeg job (GIF, source audio analysis) at a time, beside the render. Both are
    /// user-triggered and CPU-heavy; letting them stack would starve the renders they sit next to.
    /// </summary>
    private readonly SemaphoreSlim _adhocGate = new(1, 1);

    [LoggerMessage(Level = LogLevel.Information, Message = "FFmpeg render complete for session {MediaId}")]
    private partial void LogRenderComplete(MediaId mediaId);

    [LoggerMessage(Level = LogLevel.Information, Message = "ffprobe: session {MediaId} actual output duration = {Duration:F2}s")]
    private partial void LogProbeDuration(MediaId mediaId, double duration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ffprobe duration unavailable for session {MediaId}")]
    private partial void LogProbeUnavailable(MediaId mediaId);

    private readonly BlobStorageService _blobService;
    private readonly FFmpegProcess _ffmpeg;
    private readonly ILogger<FFmpegRenderService> _logger;

    public FFmpegRenderService(BlobStorageService blobService, FFmpegProcess ffmpeg, ILogger<FFmpegRenderService> logger)
    {
        _blobService = blobService;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>
    /// Renders the job and uploads the output, its poster frame included. Returns the output's
    /// duration as ffprobe measured it, or 0 when that is unavailable.
    /// </summary>
    public async Task<double> RenderAsync(RenderJob job, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting FFmpeg render for session {MediaId} with {SoundCount} sound(s)",
            job.MediaId, job.Cues.Count);

        var tempDir = Path.Combine(Path.GetTempPath(), $"{"poredomedia"}-{job.MediaId}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // ── 1. Download source video from blob ────────────────────────────
            var sourceExt = Path.GetExtension(job.SourceBlobPath).TrimStart('.');
            var sourcePath = Path.Combine(tempDir, $"source.{sourceExt}");
            await DownloadBlobToFileAsync(job.SourceBlobPath, sourcePath, cancellationToken);
            _logger.LogInformation("Source video downloaded: {Path}", sourcePath);

            // Probe the true source duration so the render can be hard-trimmed to match it
            // (prevents trailing meme audio from extending the output past the video).
            var sourceDurationSeconds = await _ffmpeg.DurationSecondsAsync(sourcePath, cancellationToken);

            // Probe for an audio stream so we only try to mix the original audio when it exists
            // (referencing [0:a] on a silent video would fail the filter graph).
            var sourceHasAudio = await _ffmpeg.HasAudioStreamAsync(sourcePath, cancellationToken);

            // ── 2. Download each sound file ───────────────────────────────────
            var renderEntries = new List<RenderVisualEntry>();
            for (var i = 0; i < job.Cues.Count; i++)
            {
                var cue = job.Cues[i];
                var soundExt = Path.GetExtension(cue.SoundPath);
                if (string.IsNullOrEmpty(soundExt)) soundExt = ".mp3";
                var soundPath = Path.Combine(tempDir, $"sound_{i}{soundExt}");

                try
                {
                    await DownloadBlobToFileAsync(cue.SoundPath, soundPath, cancellationToken);
                    if (!await _ffmpeg.HasAudioStreamAsync(soundPath, cancellationToken))
                    {
                        _logger.LogWarning("Sound {Index} ({Path}) has no valid audio stream — skipping", i, soundPath);
                        continue;
                    }
                    renderEntries.Add(cue with { SoundPath = soundPath });
                    _logger.LogDebug("Sound {Index} downloaded: {Path}", i, soundPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not download sound {Index} ({Url}) — skipping", i, cue.SoundPath);
                }
            }

            // Effective output duration respects trimming if specified
            var effectiveDuration = job.TrimDurationSeconds.HasValue && job.TrimDurationSeconds.Value > 0
                ? job.TrimDurationSeconds.Value
                : sourceDurationSeconds;

            // ── 3. Build FFmpeg command ───────────────────────────────────────
            var outputPath = Path.Combine(tempDir, "output.mp4");
            var args = FFmpegArgs.BuildFFmpegArgs(
                sourcePath,
                renderEntries,
                outputPath,
                job.AggressiveVisuals,
                effectiveDuration,
                sourceHasAudio,
                job.TrimStartSeconds,
                job.AspectRatio,
                job.Subtitles);

            _logger.LogDebug("FFmpeg args: {Args}", args);

            // ── 4. Run FFmpeg ─────────────────────────────────────────────────
            var exitCode = await _ffmpeg.RunAsync(args, job.MediaId, cancellationToken);
            if (exitCode != 0)
                throw new InvalidOperationException($"FFmpeg exited with code {exitCode} for session {job.MediaId}.");

            LogRenderComplete(job.MediaId);
            // ── 4b. Probe actual output duration via ffprobe ─────────────────────────────
            var outputDurationSeconds = await _ffmpeg.DurationSecondsAsync(outputPath, cancellationToken);
            if (outputDurationSeconds > 0)
                LogProbeDuration(job.MediaId, outputDurationSeconds);
            else
                LogProbeUnavailable(job.MediaId);
            // ── 5. Upload output to blob storage ──────────────────────────────
            await _blobService.UploadFileAsync(job.OutputBlobPath, outputPath, "video/mp4", cancellationToken);
            _logger.LogInformation("Output uploaded: {Path}", job.OutputBlobPath);

            // ── 5b. A poster frame for the gallery ──
            await UploadThumbnailAsync(outputPath, job.MediaId, outputDurationSeconds, tempDir, cancellationToken);
            return outputDurationSeconds;
        }
        finally
        {
            // ── 6. Clean up temp files ────────────────────────────────────────
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete temp dir {Dir}", tempDir); }
        }
    }

    /// <summary>
    /// Poster frame for the share page / feed, one second in (frame 0 is often black). Failure is
    /// logged and swallowed: a missing thumbnail must never fail a finished render.
    /// </summary>
    private async Task UploadThumbnailAsync(string outputPath, MediaId mediaId, double outputDurationSeconds, string tempDir, CancellationToken ct)
    {
        try
        {
            var thumbPath = Path.Combine(tempDir, "thumb.jpg");
            var seek = outputDurationSeconds is > 0 and < 2 ? 0 : 1;
            var exit = await _ffmpeg.RunAsync(
                $"-ss {seek} -i \"{outputPath}\" -frames:v 1 -vf \"scale=640:-2\" -q:v 4 -y \"{thumbPath}\"",
                mediaId,
                ct);
            if (exit == 0 && File.Exists(thumbPath))
                await _blobService.UploadFileAsync(SessionBlobPaths.Thumbnail(mediaId.Value), thumbPath, "image/jpeg", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogThumbnailFailed(ex, mediaId);
        }
    }

    /// <summary>Envelope resolution: 20 ms frames, fine enough to land a cue on a hit.</summary>
    internal const int EnvelopeFramesPerSecond = 50;

    public async Task<SourceMedia> AnalyseSourceAudioAsync(
        string sourceBlobPath,
        string? speechDestinationPath,
        CancellationToken cancellationToken = default)
    {
        await _adhocGate.WaitAsync(cancellationToken);
        var workPath = Path.Combine(Path.GetTempPath(), $"{"poredomedia"}-audio-{Guid.NewGuid():N}");
        var sourcePath = workPath + Path.GetExtension(sourceBlobPath);
        var pcmPath = workPath + ".pcm";
        try
        {
            await DownloadBlobToFileAsync(sourceBlobPath, sourcePath, cancellationToken);
            var durationSeconds = await _ffmpeg.DurationSecondsAsync(sourcePath, cancellationToken);
            if (!await _ffmpeg.HasAudioStreamAsync(sourcePath, cancellationToken))
                return new SourceMedia(durationSeconds, null);

            var exit = await _ffmpeg.RunAsync(
                FFmpegArgs.BuildSourceAudioArgs(sourcePath, speechDestinationPath, pcmPath),
                MediaId.Empty,
                cancellationToken);
            if (exit != 0 || !File.Exists(pcmPath))
                return new SourceMedia(durationSeconds, null);

            var pcm = await File.ReadAllBytesAsync(pcmPath, cancellationToken);
            return new SourceMedia(durationSeconds, new AudioEnvelope(
                ComputeRmsEnvelope(pcm, FFmpegArgs.EnvelopeSampleRate, EnvelopeFramesPerSecond),
                EnvelopeFramesPerSecond));
        }
        finally
        {
            foreach (var path in new[] { sourcePath, pcmPath })
            {
                try { File.Delete(path); }
                catch (IOException ex) { _logger.LogWarning(ex, "Could not delete temp file {Path}", path); }
            }
            _adhocGate.Release();
        }
    }

    /// <summary>RMS of little-endian signed 16-bit mono PCM, normalised to 0–1, per frame.</summary>
    internal static float[] ComputeRmsEnvelope(ReadOnlySpan<byte> pcm, int sampleRate, int framesPerSecond)
    {
        var samplesPerFrame = Math.Max(1, sampleRate / framesPerSecond);
        var sampleCount = pcm.Length / 2;
        var frames = new float[(sampleCount + samplesPerFrame - 1) / samplesPerFrame];
        for (var f = 0; f < frames.Length; f++)
        {
            var start = f * samplesPerFrame;
            var end = Math.Min(sampleCount, start + samplesPerFrame);
            double sum = 0;
            for (var i = start; i < end; i++)
            {
                double sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2)) / 32768.0;
                sum += sample * sample;
            }
            frames[f] = (float)Math.Sqrt(sum / Math.Max(1, end - start));
        }
        return frames;
    }

    private async Task DownloadBlobToFileAsync(string blobPath, string destPath, CancellationToken ct)
    {
        await using var blobStream = await _blobService.OpenReadAsync(blobPath, ct);
        await using var fileStream = File.Create(destPath);
        await blobStream.CopyToAsync(fileStream, ct);
    }
}
