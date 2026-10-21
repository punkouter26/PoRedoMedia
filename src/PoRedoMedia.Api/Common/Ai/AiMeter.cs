using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>
/// Counts what every AI call costs: one log line per call, two metrics, and a running total for
/// the run the call was made in. Token-billed calls are priced from
/// <c>AiPricing:Models:{model}</c> (USD per million tokens: <c>Input</c>, <c>CachedInput</c>,
/// <c>Output</c>); a model without prices there falls back to the flat per-call estimate the
/// Create page also shows (<c>AiPricing:TextUsd</c> and friends). Every figure is an estimate.
/// </summary>
/// <remarks>
/// Calls reach here three ways: <see cref="AiFoundryClient"/> for the Foundry chat calls,
/// <see cref="MeteredChatClient"/> for everything behind <see cref="IChatClient"/>, and
/// <see cref="AiMeterHandler"/> on the HTTP clients of the providers that are plain REST
/// (Gemini, Veo, Lyria, fal, speech).
/// </remarks>
public sealed partial class AiMeter(IConfiguration configuration, ILogger<AiMeter> logger)
{
    private static readonly AsyncLocal<AiTally?> Current = new();
    private static readonly Meter Meter = new(PoTelemetry.Name);
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>("poredomedia.ai.calls", description: "AI provider calls");
    private static readonly Counter<double> Cost = Meter.CreateCounter<double>("poredomedia.ai.cost", "USD", "Estimated AI spend");

    [LoggerMessage(Level = LogLevel.Information,
        Message = "AI usage: Stage={AiStage} Step={AiStep} Model={AiModel} InputTokens={InputTokens} CachedInputTokens={CachedInputTokens} OutputTokens={OutputTokens} ElapsedMs={ElapsedMs} EstimatedCostUsd={EstimatedCostUsd}")]
    private partial void LogTokens(string aiStage, string aiStep, string aiModel, long inputTokens, long cachedInputTokens, long outputTokens, long elapsedMs, decimal? estimatedCostUsd);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "AI usage: Stage={AiStage} Step={AiStep} Model={AiModel} Units={Units} ElapsedMs={ElapsedMs} EstimatedCostUsd={EstimatedCostUsd}")]
    private partial void LogCall(string aiStage, string aiStep, string aiModel, double units, long elapsedMs, decimal? estimatedCostUsd);

    /// <summary>
    /// Starts counting the calls made from here on in this async flow, which is one run. The count
    /// ends with the async method that began it.
    /// </summary>
    public static AiTally Begin()
    {
        var tally = new AiTally();
        Current.Value = tally;
        return tally;
    }

    /// <summary>The run being counted in this flow, or null outside a run.</summary>
    public static AiTally? Tally => Current.Value;

    /// <summary>
    /// Counts this flow's calls toward <paramref name="tally"/>. For work handed to code that does
    /// not carry the caller's async flow, such as a cache's factory.
    /// </summary>
    public static void Join(AiTally? tally) => Current.Value = tally;

    /// <summary>Records a token-billed call. Returns its estimated cost, or null when nothing prices it.</summary>
    public decimal? RecordTokens(
        string stage, string model, long inputTokens, long cachedInputTokens, long outputTokens, long elapsedMs, string fallbackPrice = "TextUsd")
    {
        var cost = TokenCost(model, inputTokens, cachedInputTokens, outputTokens) ?? Flat(fallbackPrice);
        LogTokens(stage, Step, model, inputTokens, cachedInputTokens, outputTokens, elapsedMs, cost);
        Count(stage, model, cost);
        return cost;
    }

    /// <summary>
    /// Records a call billed per item (an image, a clip, a song) or per unit (minutes of audio):
    /// <c>AiPricing:{price}</c> times <paramref name="units"/>.
    /// </summary>
    public decimal? RecordCall(string stage, string model, string price, long elapsedMs, double units = 1)
    {
        var cost = Flat(price) is { } each ? Math.Round(each * (decimal)units, 6) : (decimal?)null;
        LogCall(stage, Step, model, units, elapsedMs, cost);
        Count(stage, model, cost);
        return cost;
    }

    /// <summary>USD for a token count from <c>AiPricing:Models:{model}</c>, or the older <c>AiFoundry:Pricing:{model}</c>.</summary>
    internal decimal? TokenCost(string model, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        var section = configuration.GetSection($"AiPricing:Models:{model}");
        if (section.GetValue<decimal?>("Input") is null)
            section = configuration.GetSection($"AiFoundry:Pricing:{model}");

        var input = section.GetValue<decimal?>("Input");
        var output = section.GetValue<decimal?>("Output");
        if (input is null || output is null)
            return null;

        var cachedPrice = section.GetValue<decimal?>("CachedInput") ?? input.Value;
        var uncached = Math.Max(0, inputTokens - cachedInputTokens);
        return Math.Round((uncached * input.Value + cachedInputTokens * cachedPrice + outputTokens * output.Value) / 1_000_000m, 6);
    }

    private decimal? Flat(string price) => configuration.GetValue<decimal?>($"AiPricing:{price}");

    private static string Step => Current.Value?.Step ?? "-";

    private static void Count(string stage, string model, decimal? cost)
    {
        Current.Value?.Add(cost);
        var tags = new TagList { { "stage", stage }, { "model", model } };
        Calls.Add(1, tags);
        if (cost is { } usd)
            Cost.Add((double)usd, tags);
    }
}

/// <summary>What one run's AI calls cost so far. Thread-safe: a step may call several models at once.</summary>
public sealed class AiTally
{
    private readonly Lock _lock = new();
    private decimal _usd;
    private int _calls;
    private int _unpriced;

    /// <summary>The step being run, for the usage log.</summary>
    public string? Step { get; set; }

    public decimal Usd { get { lock (_lock) return _usd; } }

    public int Calls { get { lock (_lock) return _calls; } }

    /// <summary>Calls nothing put a price on; the total leaves them out.</summary>
    public int Unpriced { get { lock (_lock) return _unpriced; } }

    internal void Add(decimal? usd)
    {
        lock (_lock)
        {
            _calls++;
            if (usd is { } value)
                _usd += value;
            else
                _unpriced++;
        }
    }

    /// <summary>One line for the run's log, or null when the run made no AI call.</summary>
    public string? Summary()
    {
        lock (_lock)
        {
            if (_calls == 0)
                return null;
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"AI cost ≈ ${_usd:0.0000} across {_calls} call(s)") + (_unpriced > 0 ? $", {_unpriced} unpriced" : "");
        }
    }
}

/// <summary>Meters every call that goes through an <see cref="IChatClient"/>, from the usage the response reports.</summary>
public sealed class MeteredChatClient(IChatClient inner, AiMeter meter, string stage) : DelegatingChatClient(inner)
{
    // The deployment name, which is what prices are keyed by; the response names the dated model behind it.
    private readonly string? _model = inner.GetService<ChatClientMetadata>()?.DefaultModelId;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var response = await base.GetResponseAsync(list, options, cancellationToken);
        var sawImage = list.Any(m => m.Contents.OfType<DataContent>().Any(d => d.HasTopLevelMediaType("image")));
        meter.RecordTokens(
            stage, options?.ModelId ?? _model ?? response.ModelId ?? "unknown",
            response.Usage?.InputTokenCount ?? 0, response.Usage?.CachedInputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, sawImage ? "VisionUsd" : "TextUsd");
        return response;
    }
}

/// <summary>
/// Meters the REST providers by the requests that bill: Gemini <c>generateContent</c> (tokens for
/// text and vision, a flat price per picture for the image models), Veo job starts, Lyria songs,
/// fal background removals and speech clips. Only answered requests count.
/// </summary>
public sealed class AiMeterHandler(AiMeter meter, IConfiguration configuration) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await base.SendAsync(request, cancellationToken);
        if (request.Method != HttpMethod.Post || !response.IsSuccessStatusCode || request.RequestUri is not { } uri)
            return response;

        var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var path = uri.AbsolutePath;
        if (path.EndsWith(":generateContent", StringComparison.Ordinal))
        {
            var model = ModelOf(path);
            if (model.Contains("image", StringComparison.OrdinalIgnoreCase))
            {
                var upscale = configuration[ConfigKeys.GoogleUpscaleModel] is { Length: > 0 } u ? u : "gemini-3-pro-image";
                meter.RecordCall("image", model, model == upscale ? "UpscaleUsd" : "ImageUsd", elapsed);
            }
            else
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                var (input, cached, output) = GeminiUsage(await response.Content.ReadAsByteArrayAsync(cancellationToken));
                meter.RecordTokens("gemini", model, input, cached, output, elapsed, "VisionUsd");
            }
        }
        else if (path.EndsWith(":predictLongRunning", StringComparison.Ordinal))
        {
            meter.RecordCall("video", ModelOf(path), "VideoUsd", elapsed);
        }
        else if (path.EndsWith("/interactions", StringComparison.Ordinal))
        {
            meter.RecordCall("music", configuration[ConfigKeys.GoogleLyriaModel] ?? "lyria", "MusicUsd", elapsed);
        }
        else if (uri.Host.StartsWith("router.huggingface", StringComparison.OrdinalIgnoreCase))
        {
            // The submit is the billed request; status polls and the result read are GETs.
            meter.RecordCall("cutout", "bria-rmbg", "CutoutUsd", elapsed);
        }
        else if (path.Contains("/audio/speech", StringComparison.Ordinal) || path.EndsWith("/tts/cognitiveservices/v1", StringComparison.Ordinal))
        {
            meter.RecordCall("speech", path.Contains("/audio/speech", StringComparison.Ordinal) ? "openai-tts" : "azure-speech", "SpeechUsd", elapsed);
        }

        return response;
    }

    /// <summary><c>/v1beta/models/gemini-x:generateContent</c> → <c>gemini-x</c>.</summary>
    internal static string ModelOf(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var colon = name.IndexOf(':');
        return colon > 0 ? name[..colon] : name;
    }

    /// <summary>The token counts in a Gemini answer's <c>usageMetadata</c>; zeros when it has none.</summary>
    internal static (long Input, long Cached, long Output) GeminiUsage(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("usageMetadata", out var usage))
                return (0, 0, 0);
            long Read(string name) => usage.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            return (Read("promptTokenCount"), Read("cachedContentTokenCount"), Read("candidatesTokenCount") + Read("thoughtsTokenCount"));
        }
        catch (JsonException)
        {
            return (0, 0, 0);
        }
    }
}
