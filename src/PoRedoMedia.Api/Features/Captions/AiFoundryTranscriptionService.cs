// GoF: Adapter — wraps the Foundry audio-transcription endpoint
using System.Text.Json;
using PoRedoMedia.Api.Common.Ai;
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

    // Whisper is reached on the deployment path with an api-version. Every other call in this app
    // uses the resource's /openai/v1 path, but that path answered DeploymentNotFound for a whisper
    // deployment the deployment path served at once (checked 2026-10-05, po-aiservices-shared).
    private const string ApiVersion = "2024-06-01";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly Uri? _url;
    private readonly string? _key;
    private readonly string _deployment;
    private readonly ILogger<AiFoundryTranscriptionService> _logger;

    public AiFoundryTranscriptionService(IConfiguration config, ILogger<AiFoundryTranscriptionService> logger)
    {
        _logger = logger;
        _deployment = config["AiFoundry:TranscriptionDeployment"] ?? string.Empty;
        _key = AiFoundryClient.Setting(config, "AiFoundry:Key");
        if (AiFoundryClient.Setting(config, "AiFoundry:Endpoint") is { } endpoint && _key is not null && !string.IsNullOrWhiteSpace(_deployment))
            _url = new Uri($"{endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(_deployment)}/audio/transcriptions?api-version={ApiVersion}");
    }

    public bool IsEnabled => _url is not null;

    public async Task<IReadOnlyList<TranscriptSegmentDto>> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        if (_url is null)
            return [];

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var audio = File.OpenRead(audioFilePath);
        using var form = new MultipartFormDataContent
        {
            { new StreamContent(audio), "file", Path.GetFileName(audioFilePath) },
            { new StringContent("verbose_json"), "response_format" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = form };
        request.Headers.Add("api-key", _key);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Transcription failed ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 300)]}", null, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        var (segments, seconds) = Read(json.RootElement);
        LogUsage(_deployment, seconds, segments.Count, sw.ElapsedMilliseconds);
        return segments;
    }

    /// <summary>The timed lines of a <c>verbose_json</c> reply, without the ones Whisper itself marks as not speech.</summary>
    internal static (List<TranscriptSegmentDto> Segments, double Seconds) Read(JsonElement reply)
    {
        var segments = new List<TranscriptSegmentDto>();
        if (reply.TryGetProperty("segments", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in list.EnumerateArray())
            {
                var text = s.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
                var noSpeech = s.TryGetProperty("no_speech_prob", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : 0;
                if (!string.IsNullOrWhiteSpace(text) && noSpeech <= MaxNoSpeechProbability)
                    segments.Add(new TranscriptSegmentDto(s.GetProperty("start").GetDouble(), s.GetProperty("end").GetDouble(), text));
            }
        }

        return (segments, reply.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0);
    }
}
