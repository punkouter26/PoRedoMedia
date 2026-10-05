// GoF: Proxy — a caching, de-duplicating stand-in for the expensive source audio analysis
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Captions;

/// <summary>The source's audio as the engine needs it: speech lines and the loudness envelope, in source time.</summary>
/// <param name="SpeechError">Why speech is missing although transcription is configured; null otherwise.</param>
/// <param name="DurationSeconds">The whole source's length as ffprobe measured it; 0 when unknown.</param>
public sealed record SourceAudio(
    bool HasAudio,
    IReadOnlyList<TranscriptSegmentDto> Speech,
    AudioEnvelope? Envelope,
    string? SpeechError,
    double DurationSeconds = 0)
{
    public static readonly SourceAudio None = new(false, [], null, null);

    /// <summary>The same audio seen through a trim window: times shift so the window starts at 0.</summary>
    public SourceAudio Trim(double startSeconds, double durationSeconds)
    {
        var end = startSeconds + durationSeconds;
        var speech = Speech
            .Where(s => s.EndSeconds > startSeconds && s.StartSeconds < end)
            .Select(s => s with
            {
                StartSeconds = Math.Max(0, s.StartSeconds - startSeconds),
                EndSeconds = Math.Min(durationSeconds, s.EndSeconds - startSeconds),
            })
            .ToList();

        AudioEnvelope? envelope = null;
        if (Envelope is { } env)
        {
            var from = Math.Clamp((int)(startSeconds * env.FramesPerSecond), 0, env.Rms.Length);
            var to = Math.Clamp((int)Math.Ceiling(end * env.FramesPerSecond), from, env.Rms.Length);
            envelope = env with { Rms = env.Rms[from..to] };
        }

        return this with { Speech = speech, Envelope = envelope };
    }
}

/// <summary>
/// Runs speech-to-text and loudness analysis once per session, starting as soon as the upload is
/// confirmed, so both are usually finished before the user presses INITIATE.
/// </summary>
/// <remarks>
/// Transcription used to run inside the engine, after vision and before the director — every
/// second of it was added to the wait on the Engine page. The analysis covers the whole source
/// (the trim is not known yet at upload) and is shifted to the trim window when read. Results
/// persist beside the session, so a retry or a host restart does not pay for them again; a run
/// that failed persists nothing, so the next read retries it.
/// </remarks>
public sealed partial class SourceAudioAnalysis : ISourceAudioAnalysis
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {MediaId}: source audio analysis failed")]
    private partial void LogAnalysisFailed(Exception ex, MediaId mediaId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {MediaId}: transcription failed; continuing without speech")]
    private partial void LogTranscriptionFailed(Exception ex, MediaId mediaId);

    /// <summary>Ceiling for one background analysis (download + decode + transcription).</summary>
    private static readonly TimeSpan AnalysisTimeout = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<MediaId, Lazy<Task<SourceAudio>>> _inFlight = new();
    private readonly IMediaToolkit _media;
    private readonly ITranscriptionService _transcription;
    private readonly BlobStorageService _blobs;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SourceAudioAnalysis> _logger;

    public SourceAudioAnalysis(
        IMediaToolkit media,
        ITranscriptionService transcription,
        BlobStorageService blobs,
        IHostApplicationLifetime lifetime,
        ILogger<SourceAudioAnalysis> logger)
    {
        _media = media;
        _transcription = transcription;
        _blobs = blobs;
        _lifetime = lifetime;
        _logger = logger;
    }

    public void Prefetch(MediaId mediaId, string sourceBlobPath)
    {
        if (string.IsNullOrWhiteSpace(sourceBlobPath))
            return;
        _ = Start(mediaId, sourceBlobPath);
    }

    /// <summary>The analysis: joins a running one, else reads the stored result, else runs it now.</summary>
    public async Task<SourceAudio> GetAsync(MediaId mediaId, string sourceBlobPath, CancellationToken cancellationToken)
    {
        try
        {
            return await Start(mediaId, sourceBlobPath).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogAnalysisFailed(ex, mediaId);
            return SourceAudio.None;
        }
    }

    private Task<SourceAudio> Start(MediaId mediaId, string sourceBlobPath)
    {
        var lazy = _inFlight.GetOrAdd(mediaId, id => new Lazy<Task<SourceAudio>>(() => RunAsync(id, sourceBlobPath)));
        return lazy.Value;
    }

    private async Task<SourceAudio> RunAsync(MediaId mediaId, string sourceBlobPath)
    {
        // Detached from the request that started it: the upload page moves on long before this ends.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
        cts.CancelAfter(AnalysisTimeout);
        var ct = cts.Token;
        try
        {
            await Task.Yield();
            return await LoadStoredAsync(mediaId, ct) ?? await AnalyseAndStoreAsync(mediaId, sourceBlobPath, ct);
        }
        finally
        {
            _inFlight.TryRemove(mediaId, out _);
        }
    }

    private async Task<SourceAudio?> LoadStoredAsync(MediaId mediaId, CancellationToken ct)
    {
        var envelopePath = SessionBlobPaths.AudioEnvelope(mediaId.Value);
        if (!await _blobs.ExistsAsync(envelopePath, ct))
            return null;

        var speechPath = SessionBlobPaths.SourceSpeech(mediaId.Value);
        var speechStored = await _blobs.ExistsAsync(speechPath, ct);
        // Stored before transcription was configured: analyse again so speech is not silently missing.
        if (_transcription.IsEnabled && !speechStored)
            return null;

        List<TranscriptSegmentDto> speech = [];
        if (speechStored)
        {
            await using var stream = await _blobs.OpenReadAsync(speechPath, ct);
            speech = await JsonSerializer.DeserializeAsync<List<TranscriptSegmentDto>>(stream, JsonOpts, ct) ?? [];
        }

        await using var envelopeStream = await _blobs.OpenReadAsync(envelopePath, ct);
        using var ms = new MemoryStream();
        await envelopeStream.CopyToAsync(ms, ct);
        var durationSeconds = await LoadDurationAsync(mediaId, ct);
        if (ms.Length == 0)
            return new SourceAudio(false, speech, null, null, durationSeconds);

        var rms = MemoryMarshal.Cast<byte, float>(ms.GetBuffer().AsSpan(0, (int)ms.Length)).ToArray();
        // Analyses stored before the duration was kept: the audio track's length stands in for it.
        if (durationSeconds <= 0)
            durationSeconds = (double)rms.Length / FFmpegEnvelopeRate;
        return new SourceAudio(true, speech, new AudioEnvelope(rms, FFmpegEnvelopeRate), null, durationSeconds);
    }

    private async Task<double> LoadDurationAsync(MediaId mediaId, CancellationToken ct)
    {
        var path = SessionBlobPaths.SourceDuration(mediaId.Value);
        if (!await _blobs.ExistsAsync(path, ct))
            return 0;

        await using var stream = await _blobs.OpenReadAsync(path, ct);
        using var reader = new StreamReader(stream);
        return double.TryParse(await reader.ReadToEndAsync(ct), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
    }

    /// <summary>Must match <c>FFmpegRenderService.EnvelopeFramesPerSecond</c>, which wrote the stored envelope.</summary>
    private const int FFmpegEnvelopeRate = 50;

    private async Task<SourceAudio> AnalyseAndStoreAsync(MediaId mediaId, string sourceBlobPath, CancellationToken ct)
    {
        var speechPath = _transcription.IsEnabled
            ? Path.Combine(Path.GetTempPath(), $"{"poredomedia"}-{mediaId}-speech.mp3")
            : null;
        try
        {
            var (durationSeconds, envelope) = await _media.AnalyseSourceAudioAsync(sourceBlobPath, speechPath, ct);
            if (envelope is null)
            {
                await StoreAsync(mediaId, [], null, durationSeconds, ct);
                return SourceAudio.None with { DurationSeconds = durationSeconds };
            }

            IReadOnlyList<TranscriptSegmentDto> speech = [];
            string? speechError = null;
            if (speechPath is not null && File.Exists(speechPath))
            {
                try
                {
                    speech = await _transcription.TranscribeAsync(speechPath, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    LogTranscriptionFailed(ex, mediaId);
                    speechError = ex.Message.Contains("429", StringComparison.Ordinal) ? "rate limited" : ex.GetType().Name;
                }
            }

            // A failed transcription is not stored, so the engine's read retries it.
            if (speechError is null)
                await StoreAsync(mediaId, speech, envelope, durationSeconds, ct);

            return new SourceAudio(true, speech, envelope, speechError, durationSeconds);
        }
        finally
        {
            if (speechPath is not null)
            {
                try { File.Delete(speechPath); } catch (IOException) { }
            }
        }
    }

    private async Task StoreAsync(MediaId mediaId, IReadOnlyList<TranscriptSegmentDto> speech, AudioEnvelope? envelope, double durationSeconds, CancellationToken ct)
    {
        if (durationSeconds > 0)
        {
            using var text = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(durationSeconds.ToString("R", CultureInfo.InvariantCulture)));
            await _blobs.UploadAsync(SessionBlobPaths.SourceDuration(mediaId.Value), text, "text/plain", ct);
        }

        if (_transcription.IsEnabled)
        {
            using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(speech, JsonOpts));
            await _blobs.UploadAsync(SessionBlobPaths.SourceSpeech(mediaId.Value), json, "application/json", ct);
        }

        // Written last: its presence is what marks the analysis complete. Empty = no audio track.
        var bytes = envelope is null ? [] : MemoryMarshal.AsBytes(envelope.Rms.AsSpan()).ToArray();
        using var bin = new MemoryStream(bytes);
        await _blobs.UploadAsync(SessionBlobPaths.AudioEnvelope(mediaId.Value), bin, "application/octet-stream", ct);
    }
}
