// GoF: Adapter — wraps the Foundry audio-transcription endpoint
using OpenAI.Audio;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Captions;

/// <summary>Speech-to-text for the director. Disabled (returns nothing) when unconfigured.</summary>
/// <remarks>
/// Needs a deployment that returns <c>verbose_json</c> with segment timestamps — that is
/// <c>whisper</c>. The gpt-4o-*-transcribe models only return plain text, and without timings a
/// transcript cannot place a sound after a punchline or time a subtitle, so they are not a
/// drop-in substitute here.
///
/// Opt-in via <c>AiFoundry:TranscriptionDeployment</c>. Unset means every call is a no-op, which is
/// also what keeps test environments from reaching for a real endpoint.
/// </remarks>
public sealed partial class AiFoundryTranscriptionService : ITranscriptionService
{
    /// <summary>Whisper's own "this was silence/music" signal; above it the segment is dropped.</summary>
    private const double MaxNoSpeechProbability = 0.6;

    // Whisper bills by the second of audio, not by token, so that is what the usage line carries.
    [LoggerMessage(Level = LogLevel.Information,
        Message = "AI usage: Stage=transcription Deployment={AiDeployment} AudioSeconds={AudioSeconds:F1} Segments={SegmentCount} ElapsedMs={ElapsedMs}")]
    private partial void LogUsage(string aiDeployment, double audioSeconds, int segmentCount, long elapsedMs);

    private readonly AudioClient? _client;
    private readonly string _deployment;
    private readonly ILogger<AiFoundryTranscriptionService> _logger;

    public AiFoundryTranscriptionService(AiFoundryClient ai, IConfiguration config, ILogger<AiFoundryTranscriptionService> logger)
    {
        _logger = logger;
        _deployment = config["AiFoundry:TranscriptionDeployment"] ?? string.Empty;
        _client = ai.Audio(_deployment);
    }

    public bool IsEnabled => _client is not null;

    public async Task<IReadOnlyList<TranscriptSegmentDto>> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        if (_client is null)
            return [];

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var audio = File.OpenRead(audioFilePath);
        var result = await _client.TranscribeAudioAsync(
            audio,
            Path.GetFileName(audioFilePath),
            new AudioTranscriptionOptions
            {
                ResponseFormat = AudioTranscriptionFormat.Verbose,
                TimestampGranularities = AudioTimestampGranularities.Segment,
            },
            cancellationToken);

        var segments = result.Value.Segments
            .Where(s => s.NoSpeechProbability <= MaxNoSpeechProbability && !string.IsNullOrWhiteSpace(s.Text))
            .Select(s => new TranscriptSegmentDto(s.StartTime.TotalSeconds, s.EndTime.TotalSeconds, s.Text.Trim()))
            .ToList();

        LogUsage(_deployment, result.Value.Duration?.TotalSeconds ?? 0, segments.Count, sw.ElapsedMilliseconds);
        return segments;
    }
}
