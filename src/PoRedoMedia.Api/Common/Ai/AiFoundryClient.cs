using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Diagnostics;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// The Foundry chat and audio clients, built once, plus the two things every AI call
/// needs: a strict-JSON completion with a fallback, and token/cost logging.
/// </summary>
/// <remarks>
/// Director, vision and transcription each built their own client with
/// their own endpoint rules. Only vision honoured the test interception, only vision had a 429
/// policy, and the director had no timeout in production. Share suggestions, sound tagging and
/// the studio's director assist also need AI, from three different slices; a cross-cutting facade
/// in Common is how they get it without reaching into Processing.
/// </remarks>
public sealed partial class AiFoundryClient
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "AI usage: Stage={AiStage} Deployment={AiDeployment} Session={AiSessionId} InputTokens={InputTokens} CachedInputTokens={CachedInputTokens} OutputTokens={OutputTokens} ElapsedMs={ElapsedMs} EstimatedCostUsd={EstimatedCostUsd}")]
    private partial void LogUsage(string aiStage, string aiDeployment, string aiSessionId, int inputTokens, int cachedInputTokens, int outputTokens, long elapsedMs, decimal? estimatedCostUsd);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Deployment {AiDeployment} rejected a strict JSON schema; falling back to JSON-object mode for it from now on.")]
    private partial void LogSchemaUnsupported(Exception ex, string aiDeployment);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "AI answer truncated at the output cap: Stage={AiStage} Deployment={AiDeployment} MaxOutputTokens={MaxOutputTokens}. The JSON is probably incomplete.")]
    private partial void LogTruncated(string aiStage, string aiDeployment, int? maxOutputTokens);

    /// <summary>Default per-attempt network timeout. The SDK's own default is 100 s per attempt.</summary>
    private const int DefaultTimeoutSeconds = 60;

    private const string FallbackDeployment = "gpt-5.4-nano";

    private readonly OpenAIClient? _standard;
    private readonly OpenAIClient? _failFast;
    private readonly IConfiguration _config;
    private readonly ILogger<AiFoundryClient> _logger;

    /// <summary>Deployments that rejected <c>json_schema</c>; they get <c>json_object</c> instead.</summary>
    private readonly ConcurrentDictionary<string, byte> _noStrictSchema = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Deployments that rejected a temperature (reasoning models such as o3 / o4-mini).</summary>
    private readonly ConcurrentDictionary<string, byte> _noTemperature = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Deployments that rejected an output-token cap; they are called without one.</summary>
    private readonly ConcurrentDictionary<string, byte> _noOutputCap = new(StringComparer.OrdinalIgnoreCase);

    public AiFoundryClient(IConfiguration config, IHostEnvironment environment, ILogger<AiFoundryClient> logger)
    {
        _config = config;
        _logger = logger;

        var endpoint = Setting(config, "AiFoundry:Endpoint");
        var key = Setting(config, "AiFoundry:Key");
        if (endpoint is null || key is null)
            return;

        var timeout = TimeSpan.FromSeconds(config.GetValue<int?>("AiFoundry:TimeoutSeconds") ?? DefaultTimeoutSeconds);

        _standard = Build(endpoint, key, timeout, maxRetries: 2);
        // No SDK retries: callers on an interactive path (vision during upload) cap their own
        // backoff instead of waiting out a 30 s+ Retry-After inside the SDK.
        _failFast = Build(endpoint, key, timeout, maxRetries: 0);
    }

    public bool IsConfigured => _standard is not null;

    /// <summary>
    /// The chat deployment the director and every other text call use (<c>AiFoundry:Deployment</c>).
    /// </summary>
    /// <remarks>
    /// It was a runtime-mutable singleton switched from a ⚙-menu dropdown through an anonymous
    /// <c>PUT /api/config/ai-model</c>, so any visitor could change the model for everyone. Changing
    /// an App Service setting restarts the app, which is all a model switch needs.
    /// </remarks>
    public string Deployment => Setting(_config, "AiFoundry:Deployment") ?? FallbackDeployment;

    /// <param name="failFast">Skip SDK retries; the caller handles throttling itself.</param>
    public ChatClient Chat(string deployment, bool failFast = false)
        => (failFast ? _failFast : _standard)?.GetChatClient(deployment)
           ?? throw new InvalidOperationException("AiFoundry:Endpoint is not configured.");

    /// <summary>
    /// A completion constrained to the call's schema (strict Structured Outputs). A deployment
    /// that rejects <c>json_schema</c> — some non-OpenAI models on Foundry do — is remembered and
    /// answered in <c>json_object</c> mode, whose output the callers' tolerant parsers still read;
    /// one that rejects a temperature (reasoning models) or the output cap is remembered and called
    /// without it. Returns the raw JSON text.
    /// </summary>
    public async Task<string> CompleteJsonAsync(
        AiCall call,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var client = Chat(call.Deployment, call.FailFast);
        var sw = Stopwatch.StartNew();

        // At most one retry per unsupported option; each refusal is remembered for the deployment.
        ClientResult<ChatCompletion> result;
        for (var attempt = 0; ; attempt++)
        {
            var strict = !_noStrictSchema.ContainsKey(call.Deployment);
            var temperature = _noTemperature.ContainsKey(call.Deployment) ? null : call.Temperature;
            var maxOutputTokens = _noOutputCap.ContainsKey(call.Deployment) ? null : call.MaxOutputTokens;
            try
            {
                result = await client.CompleteChatAsync(messages, BuildOptions(call, strict, temperature, maxOutputTokens), cancellationToken);
                break;
            }
            catch (ClientResultException ex) when (ex.Status == 400 && attempt < 3)
            {
                if (strict && Mentions(ex, "response_format", "json_schema"))
                {
                    _noStrictSchema.TryAdd(call.Deployment, 0);
                    LogSchemaUnsupported(ex, call.Deployment);
                }
                else if (temperature is not null && Mentions(ex, "temperature"))
                {
                    _noTemperature.TryAdd(call.Deployment, 0);
                }
                else if (maxOutputTokens is not null && Mentions(ex, "max_tokens", "max_completion_tokens"))
                {
                    _noOutputCap.TryAdd(call.Deployment, 0);
                }
                else
                {
                    throw;
                }
            }
        }

        sw.Stop();
        RecordUsage(call.Stage, call.Deployment, call.MediaId, result.Value.Usage, sw.ElapsedMilliseconds);
        if (result.Value.FinishReason == ChatFinishReason.Length)
            LogTruncated(call.Stage, call.Deployment, call.MaxOutputTokens);
        return result.Value.Content.Count > 0 ? result.Value.Content[0].Text.Trim() : string.Empty;
    }

    /// <summary>Logs one call's token usage, with a cost estimate when prices are configured.</summary>
    public void RecordUsage(string stage, string deployment, MediaId? mediaId, ChatTokenUsage? usage, long elapsedMs)
    {
        if (usage is null)
            return;

        var cached = usage.InputTokenDetails?.CachedTokenCount ?? 0;
        LogUsage(stage, deployment, mediaId?.ToString() ?? "-", usage.InputTokenCount, cached,
            usage.OutputTokenCount, elapsedMs,
            EstimateCostUsd(deployment, usage.InputTokenCount, cached, usage.OutputTokenCount));
    }

    /// <summary>
    /// USD from <c>AiFoundry:Pricing:{deployment}</c> (per million tokens: <c>Input</c>,
    /// <c>CachedInput</c>, <c>Output</c>). Null when the deployment has no prices configured —
    /// prices change too often to hard-code, and a wrong number is worse than none.
    /// </summary>
    internal decimal? EstimateCostUsd(string deployment, int inputTokens, int cachedInputTokens, int outputTokens)
    {
        var section = _config.GetSection($"AiFoundry:Pricing:{deployment}");
        var input = section.GetValue<decimal?>("Input");
        var output = section.GetValue<decimal?>("Output");
        if (input is null || output is null)
            return null;

        var cachedPrice = section.GetValue<decimal?>("CachedInput") ?? input.Value;
        var uncached = Math.Max(0, inputTokens - cachedInputTokens);
        return Math.Round(
            (uncached * input.Value + cachedInputTokens * cachedPrice + outputTokens * output.Value) / 1_000_000m, 6);
    }

    private static ChatCompletionOptions BuildOptions(AiCall call, bool strict, float? temperature, int? maxOutputTokens)
    {
        var options = new ChatCompletionOptions
        {
            ResponseFormat = strict
                ? ChatResponseFormat.CreateJsonSchemaFormat(call.SchemaName, BinaryData.FromString(call.Schema), jsonSchemaIsStrict: true)
                : ChatResponseFormat.CreateJsonObjectFormat(),
        };
        if (temperature is { } t)
            options.Temperature = t;
        if (maxOutputTokens is { } max)
        {
            // A deployment that rejects the cap is remembered and called uncapped.
            options.MaxOutputTokenCount = max;
        }
        return options;
    }

    private static bool Mentions(ClientResultException ex, params string[] terms)
        => terms.Any(t => ex.Message.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The resource is called through the plain OpenAI SDK on Azure's v1 endpoint, which takes the
    /// resource key as a bearer token. Azure's own SDK is not used: its only stable release is
    /// built for an OpenAI SDK far older than the one the rest of the app needs, and the two
    /// cannot load together.
    /// </summary>
    private static OpenAIClient Build(string endpoint, string key, TimeSpan timeout, int maxRetries) =>
        new(new ApiKeyCredential(key), new OpenAIClientOptions
        {
            Endpoint = AzureV1Endpoint(endpoint),
            NetworkTimeout = timeout,
            RetryPolicy = new ClientRetryPolicy(maxRetries),
        });

    /// <summary><c>https://name.cognitiveservices.azure.com/</c> becomes <c>.../openai/v1/</c>.</summary>
    public static Uri AzureV1Endpoint(string resourceEndpoint) =>
        resourceEndpoint.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase)
            ? new Uri(resourceEndpoint.TrimEnd('/') + "/")
            : new Uri(resourceEndpoint.TrimEnd('/') + "/openai/v1/");

    /// <summary>
    /// The JSON inside a Markdown code fence, or the text itself when it has none. Deployments in
    /// <c>json_object</c> mode sometimes fence their answer; every parser of a completion goes
    /// through this one copy.
    /// </summary>
    public static string StripCodeFence(string text)
    {
        var json = text.Trim();
        if (!json.StartsWith("```", StringComparison.Ordinal))
            return json;

        var firstNewline = json.IndexOf('\n');
        json = firstNewline >= 0 ? json[(firstNewline + 1)..] : json[3..];
        var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
        return (lastFence >= 0 ? json[..lastFence] : json).Trim();
    }

    /// <summary>
    /// A setting, or null when it is unset. An unset key read through <see cref="IConfiguration"/>
    /// arrives as "" (appsettings.json ships every secret that way), which plain <c>??</c> would let win.
    /// </summary>
    internal static string? Setting(IConfiguration config, string key)
        => string.IsNullOrWhiteSpace(config[key]) ? null : config[key];
}

/// <summary>One structured completion: what it is for, where it goes and the shape it must return.</summary>
/// <param name="Stage">Short name for usage logs (e.g. "director", "vision").</param>
/// <param name="Schema">JSON Schema text; strict mode needs every property required and no extras.</param>
public sealed record AiCall(string Stage, string Deployment, string SchemaName, string Schema)
{
    public MediaId? MediaId { get; init; }
    public float? Temperature { get; init; }

    /// <summary>
    /// Ceiling on the answer, so a runaway completion cannot bill without bound. Keep it generous:
    /// reasoning deployments count their reasoning tokens against it, and a truncated answer is
    /// unparseable JSON.
    /// </summary>
    public int? MaxOutputTokens { get; init; }
    public bool FailFast { get; init; }
}
