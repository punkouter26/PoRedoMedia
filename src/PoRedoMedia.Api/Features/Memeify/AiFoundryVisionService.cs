using System.ClientModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

namespace PoRedoMedia.Api.Features.Memeify;

/// <remarks>
/// <para><b>Cost.</b> Frames go at <see cref="ChatImageDetailLevel.Low"/>: a flat, small token cost
/// per image instead of several high-detail tiles each. Labels are scene-level ("man slips on
/// ice"), which a 512 px view answers as well as a full-resolution one.</para>
/// <para><b>Timing.</b> Every image is preceded by its own timestamp and the model answers with a
/// frame index, so the server — not the model — decides when a label happens. The old prompt
/// described the batch as "starting at t" but its example used t+3 s, and small models copy
/// examples.</para>
/// <para><b>Richer labels.</b> Each frame also gets an energy score, tags drawn from the sound
/// library's own vocabulary (so matching needs no translation from caption words to tag words),
/// and the main subject's position, which is where the director puts stickers.</para>
/// </remarks>
public sealed partial class AiFoundryVisionService : IAiVisionService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Vision ({Deployment}): {Count} label(s) from {Batches} batch(es): {Labels}")]
    private partial void LogLabels(string deployment, int count, int batches, string labels);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision ({Deployment}) JSON parse failed for batch at t={Offset:F1}s. Raw: {Text}")]
    private partial void LogParseFailed(Exception ex, string deployment, double offset, string text);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision ({Deployment}) throttled (HTTP 429) at t={Offset:F1}s. Retry {Attempt}/{Max} in {Delay}s.")]
    private partial void LogThrottled(string deployment, double offset, int attempt, int max, double delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision ({Deployment}) batch at t={Offset:F1}s failed; its {FrameCount} frame(s) get no label.")]
    private partial void LogBatchFailed(Exception ex, string deployment, double offset, int frameCount);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowDuplicateProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    internal const string Schema =
        """
        {"type":"object","additionalProperties":false,"required":["frames"],"properties":{"frames":{"type":"array","items":{
          "type":"object","additionalProperties":false,
          "required":["frame","label","energy","tags","subjectX","subjectY"],
          "properties":{
            "frame":{"type":"integer"},
            "label":{"type":"string"},
            "energy":{"type":"number"},
            "tags":{"type":"array","items":{"type":"string"}},
            "subjectX":{"type":["number","null"]},
            "subjectY":{"type":["number","null"]}}}}}}
        """;

    private const string SystemPrompt =
        """
        You are a meme director's eyes. For every video frame you are shown, return one entry:
        - frame: the frame number given before the image.
        - label: what is happening, at most 8 words (e.g. "man slips on wet floor", "cat stares at camera").
          Never leave a frame out; a quiet frame is still "static room" or "person talking".
        - energy: 0 (nothing happens) to 1 (chaos, impact, peak reaction).
        - tags: 0 to 4 tags copied exactly from the TAG VOCABULARY that fit the moment.
        - subjectX, subjectY: the main subject's centre (a face if there is one) as 0..1 of frame width and height, or null.
        """;

    private const int VisionBatchSize = 8;
    private const int MaxRetries = 2;
    private const int MaxBackoffSeconds = 4;

    /// <summary>
    /// Output cap for one batch. Eight labels are about 500 tokens; the rest is headroom for
    /// deployments that count reasoning tokens against the same cap.
    /// </summary>
    private const int MaxOutputTokens = 2_500;

    /// <summary>Most frequent library tags offered to the model; keeps the prompt bounded.</summary>
    private const int MaxVocabulary = 250;

    private readonly AiFoundryClient _ai;
    private readonly string _deployment;
    private readonly ILogger<AiFoundryVisionService> _logger;

    public AiFoundryVisionService(AiFoundryClient ai, IConfiguration config, ILogger<AiFoundryVisionService> logger)
    {
        _ai = ai;
        _logger = logger;
        _deployment = AiFoundryClient.Setting(config, "AiFoundry:VisionDeployment") ?? "gpt-5.4-mini";
    }

    public async Task<SceneLabel[]> AnalyseAsync(
        IReadOnlyList<KeyFrame> frames,
        IReadOnlyCollection<string> tagVocabulary,
        CancellationToken cancellationToken = default)
    {
        if (frames.Count == 0)
            return [];

        var vocabulary = tagVocabulary.Take(MaxVocabulary).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The vocabulary changes only with the library, so it belongs to the cacheable prefix.
        var system = vocabulary.Count > 0
            ? $"{SystemPrompt}\nTAG VOCABULARY: {string.Join(", ", vocabulary)}"
            : SystemPrompt;

        // Each batch fails on its own. Awaited as one, a single throttled batch threw away the
        // labels of every batch that had answered — and their tokens were already spent.
        using var gate = new SemaphoreSlim(3);
        var batches = frames.Chunk(VisionBatchSize).Select(async batch =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return (Labels: await AnalyseBatchAsync(system, batch, vocabulary, cancellationToken), Error: (Exception?)null);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogBatchFailed(ex, _deployment, batch[0].TimestampSeconds, batch.Length);
                return (Labels: Array.Empty<SceneLabel>(), Error: (Exception?)ex);
            }
            finally { gate.Release(); }
        }).ToList();

        var results = await Task.WhenAll(batches);
        // Nothing answered: the caller still needs the reason, to report it and fall back.
        if (results.All(r => r.Error is not null))
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(results[0].Error!);

        var labels = results.SelectMany(r => r.Labels).OrderBy(l => l.TimestampSeconds).ToArray();

        LogLabels(_deployment, labels.Length, batches.Count,
            string.Join(", ", labels.Select(l => $"t={l.TimestampSeconds:F1}s→{l.Label}")));
        return labels;
    }

    private async Task<SceneLabel[]> AnalyseBatchAsync(
        string system,
        KeyFrame[] batch,
        HashSet<string> vocabulary,
        CancellationToken cancellationToken)
    {
        var parts = new List<ChatMessageContentPart>(batch.Length * 2);
        for (var i = 0; i < batch.Length; i++)
        {
            parts.Add(ChatMessageContentPart.CreateTextPart(
                string.Create(CultureInfo.InvariantCulture, $"frame {i} (t={batch[i].TimestampSeconds:0.0}s)")));
            parts.Add(ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(batch[i].Image), batch[i].MediaType, ChatImageDetailLevel.Low));
        }

        ChatMessage[] messages = [new SystemChatMessage(system), new UserChatMessage(parts)];
        var offset = batch[0].TimestampSeconds;
        var text = await CompleteWithRetryAsync(messages, offset, cancellationToken);

        try
        {
            return Parse(text, batch, vocabulary);
        }
        catch (JsonException ex)
        {
            LogParseFailed(ex, _deployment, offset, text);
            return [];
        }
    }

    /// <summary>Maps the model's frame indices back onto the frames' own timestamps.</summary>
    internal static SceneLabel[] Parse(string text, IReadOnlyList<KeyFrame> batch, IReadOnlySet<string> vocabulary)
    {
        var json = AiFoundryClient.StripCodeFence(text);
        var items = json.StartsWith('[')
            ? JsonSerializer.Deserialize<VisionFrame[]>(json, JsonOpts)
            : JsonSerializer.Deserialize<VisionAnswer>(json, JsonOpts)?.Frames;

        return (items ?? [])
            .Where(f => f.Frame >= 0 && f.Frame < batch.Count && !string.IsNullOrWhiteSpace(f.Label))
            .DistinctBy(f => f.Frame)
            .Select(f => new SceneLabel(batch[f.Frame].TimestampSeconds, f.Label.Trim())
            {
                Energy = f.Energy is { } e && double.IsFinite(e) ? Math.Clamp(e, 0, 1) : 0.5,
                Tags = [.. (f.Tags ?? []).Where(vocabulary.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Take(4)],
                SubjectX = Unit(f.SubjectX),
                SubjectY = Unit(f.SubjectY),
            })
            .ToArray();

        static double? Unit(double? v) => v is { } x && double.IsFinite(x) ? Math.Clamp(x, 0, 1) : null;
    }

    /// <summary>
    /// Short, capped backoff on HTTP 429. Vision runs while the user waits on the upload page, so
    /// when the deployment is throttled for longer than a few seconds it fails fast to the
    /// time-based fallback rather than wait out a 30 s+ Retry-After. The client is built without
    /// SDK retries for the same reason.
    /// </summary>
    private async Task<string> CompleteWithRetryAsync(ChatMessage[] messages, double offset, CancellationToken cancellationToken)
    {
        var call = new AiCall("vision", _deployment, "vision_labels", Schema) { FailFast = true, MaxOutputTokens = MaxOutputTokens };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _ai.CompleteJsonAsync(call, messages, cancellationToken);
            }
            catch (ClientResultException ex) when (ex.Status == 429 && attempt < MaxRetries)
            {
                var delay = ResolveRetryDelay(ex, attempt);
                LogThrottled(_deployment, offset, attempt + 1, MaxRetries, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static TimeSpan ResolveRetryDelay(ClientResultException ex, int attempt)
    {
        // Exponential 1 s, 2 s; a shorter server Retry-After wins; never more than the cap.
        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var raw = ex.GetRawResponse();
        if (raw is not null
            && raw.Headers.TryGetValue("Retry-After", out var retryAfter)
            && int.TryParse(retryAfter, out var seconds)
            && seconds > 0
            && seconds < backoff.TotalSeconds)
        {
            backoff = TimeSpan.FromSeconds(seconds);
        }

        return backoff > TimeSpan.FromSeconds(MaxBackoffSeconds) ? TimeSpan.FromSeconds(MaxBackoffSeconds) : backoff;
    }

    private sealed record VisionAnswer([property: JsonPropertyName("frames")] VisionFrame[]? Frames);

    private sealed record VisionFrame(
        [property: JsonPropertyName("frame")] int Frame,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("energy")] double? Energy,
        [property: JsonPropertyName("tags")] string[]? Tags,
        [property: JsonPropertyName("subjectX")] double? SubjectX,
        [property: JsonPropertyName("subjectY")] double? SubjectY);
}
