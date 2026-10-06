using OpenAI.Chat;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoRedoMedia.Api.Features.Memeify;

public sealed partial class AiFoundryDirectorService : IDirectorService
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Media {MediaId}: AI Foundry director start. Deployment={Deployment}, Labels={LabelCount}, Candidates={CandidateCount}, HasRealVision={HasRealVision}")]
    private partial void LogStart(MediaId mediaId, string deployment, int labelCount, int candidateCount, bool hasRealVision);

    [LoggerMessage(Level = LogLevel.Error, Message = "Media {MediaId}: AI Foundry director timed out after {TimeoutSeconds}s.")]
    private partial void LogTimeout(MediaId mediaId, int timeoutSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media {MediaId}: the director gave no usable script; trying {Deployment}.")]
    private partial void LogEscalating(MediaId mediaId, string deployment);

    [LoggerMessage(Level = LogLevel.Error, Message = "Media {MediaId}: AI Foundry director returned unparseable JSON. Raw: {Raw}")]
    private partial void LogParseFailed(Exception ex, MediaId mediaId, string raw);

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        AllowDuplicateProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly string Schema = DirectorPrompt.Schema("entries", DirectorPrompt.CueKey.LabelIndex);

    /// <summary>Wall-clock ceiling for one director call, retries included.</summary>
    private const int DefaultTimeoutSeconds = 90;

    /// <summary>
    /// Output cap: a cue is about 150 tokens of JSON; the base and the margin per cue leave room
    /// for deployments that count reasoning tokens against the same cap.
    /// </summary>
    internal static int MaxOutputTokens(int cueCount) => 2_000 + 250 * cueCount;

    private readonly AiFoundryClient _ai;
    private readonly ILogger<AiFoundryDirectorService> _logger;
    private readonly int _timeoutSeconds;

    /// <summary>The model tried when the usual one gives no usable script. The vision deployment is the larger of the two.</summary>
    private readonly string? _escalationDeployment;

    public AiFoundryDirectorService(
        AiFoundryClient ai,
        IConfiguration config,
        ILogger<AiFoundryDirectorService> logger)
    {
        _ai = ai;
        _logger = logger;
        // Production used to default to 0 — no ceiling at all beyond the SDK's 100 s per attempt
        // times its retries. The engine degrades to a fallback script on timeout, so a bounded
        // wait is strictly better than a run stuck on "Directing".
        _timeoutSeconds = config.GetValue<int?>("AiFoundry:DirectorTimeoutSeconds") ?? DefaultTimeoutSeconds;
        _escalationDeployment = AiFoundryClient.Setting(config, "AiFoundry:EscalationDeployment") ?? AiFoundryClient.Setting(config, "AiFoundry:VisionDeployment");
    }

    public async Task<DirectedScript> DirectAsync(
        SceneLabel[] labels,
        IReadOnlyList<SoundAsset> topCandidates,
        MediaId mediaId,
        bool hasRealVisionData = false,
        DirectorContext? context = null,
        CancellationToken cancellationToken = default)
    {
        context ??= DirectorContext.Empty;
        var script = await DirectWithAsync(_ai.Deployment, labels, topCandidates, mediaId, hasRealVisionData, context, cancellationToken);
        if (script.Entries.Length > 0 || labels.Length == 0 || _escalationDeployment is null || _escalationDeployment == _ai.Deployment)
            return script;

        // The small model answered with nothing usable. One try on the larger one costs a few
        // cents; the alternative is a video with no captions or effects.
        LogEscalating(mediaId, _escalationDeployment);
        return await DirectWithAsync(_escalationDeployment, labels, topCandidates, mediaId, hasRealVisionData, context, cancellationToken);
    }

    private async Task<DirectedScript> DirectWithAsync(
        string deployment, SceneLabel[] labels, IReadOnlyList<SoundAsset> topCandidates, MediaId mediaId, bool hasRealVisionData,
        DirectorContext context, CancellationToken cancellationToken)
    {
        LogStart(mediaId, deployment, labels.Length, topCandidates.Count, hasRealVisionData);

        var sounds = DirectorPrompt.SerializeSoundsCompact(topCandidates, context.Favorites);
        ChatMessage[] messages =
        [
            new SystemChatMessage(DirectorPrompt.SystemPrompt),
            new UserChatMessage(DirectorPrompt.BuildUserPrompt(labels, sounds, hasRealVisionData, context.MemePersona, context.Transcript)),
        ];

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        string rawText;
        try
        {
            rawText = await _ai.CompleteJsonAsync(
                new AiCall("director", deployment, "director_script", Schema) { MediaId = mediaId, Temperature = 0.6f, MaxOutputTokens = MaxOutputTokens(labels.Length) },
                messages,
                linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            LogTimeout(mediaId, _timeoutSeconds);
            throw new TimeoutException($"AI Foundry director timed out after {_timeoutSeconds}s for media {mediaId}.");
        }

        try
        {
            return new DirectedScript(
                DirectorPrompt.ParseResponse(rawText, mediaId, JsonOpts, labels, topCandidates),
                DirectorPrompt.ReadTitle(rawText));
        }
        catch (JsonException ex)
        {
            LogParseFailed(ex, mediaId, rawText);
            return new DirectedScript([]);
        }
    }
}
