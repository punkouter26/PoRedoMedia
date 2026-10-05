using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>
/// Google Lyria 3 implementation of <see cref="IMusicGenerationService"/>.
/// Adapter pattern (GoF): wraps the Gemini <c>v1beta/interactions</c> music endpoint.
/// </summary>
/// <remarks>
/// Lyria performs the lyrics it is given, structured with <c>[Verse]</c> / <c>[Chorus]</c> section
/// tags — which is why it is used here rather than an instrumental text-to-audio model such as
/// Stable Audio, whose prompt only <em>describes</em> the audio.
/// <para>
/// Shares the <c>GeminiApi</c> named client with <see cref="GeminiImagen3Service"/>: same host,
/// same <c>Google:ApiKey</c>, so it inherits the standard resilience pipeline, the
/// <c>MockAiDelegatingHandler</c> budget guardrail, and outbound correlation headers.
/// </para>
/// </remarks>
public sealed class LyriaMusicService : IMusicGenerationService
{
    private const string InteractionsUrl = "https://generativelanguage.googleapis.com/v1beta/interactions";
    private const string DefaultModel = "lyria-3-clip-preview";

    private readonly ILogger<LyriaMusicService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly string _model;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuration[ConfigKeys.GoogleApiKey]);

    public LyriaMusicService(
        IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<LyriaMusicService> logger)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _model = configuration[ConfigKeys.GoogleLyriaModel] ?? DefaultModel;

        if (IsConfigured)
            _logger.LogInformation("Lyria music service initialized. Model={Model}", _model);
        else
            _logger.LogInformation("Google:ApiKey not configured; Lyria music generation is disabled.");
    }

    public async Task<MusicGenerationResult> GenerateAsync(
        string lyrics, string stylePrompt, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Lyria music generation is not configured. Set Google:ApiKey via Key Vault.");

        ArgumentException.ThrowIfNullOrWhiteSpace(lyrics);
        ArgumentException.ThrowIfNullOrWhiteSpace(stylePrompt);

        var start = Stopwatch.GetTimestamp();

        var body = new
        {
            model = _model,
            input = $"{stylePrompt}\n\nPerform these lyrics exactly as written:\n\n{lyrics}",
            response_format = new { type = "audio" },
        };

        var client = _httpClientFactory.CreateClient("GeminiApi");
        using var request = new HttpRequestMessage(HttpMethod.Post, InteractionsUrl);
        // Re-read the key each call so a rotated Key Vault secret is picked up by this singleton.
        request.Headers.Add("x-goog-api-key", _configuration[ConfigKeys.GoogleApiKey] ?? string.Empty);
        // IL2026: the outbound body is an anonymous type shaped to the third-party API's exact
        // contract, and System.Text.Json source generation cannot describe anonymous types. This
        // assembly is server-side only and is never trimmed, so the reflective writer is safe here.
        // Scoped to the single statement so the analyzer (Directory.Build.props) stays live elsewhere.
        #pragma warning disable IL2026
        request.Content = JsonContent.Create(body);
        #pragma warning restore IL2026

        using var response = await client.SendAsync(request, ct);
        var elapsed = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);

            // A safety-filter rejection is an expected outcome for a roast, not a fault: the caller
            // softens the lyrics and retries. Anything else is a genuine failure and throws.
            if (IsSafetyRefusal(response.StatusCode, errorBody))
            {
                _logger.LogInformation(
                    "Lyria declined the prompt (HTTP {Status}). Caller may soften and retry.",
                    (int)response.StatusCode);
                return MusicGenerationResult.FromRefusal(elapsed, ExtractMessage(errorBody));
            }

            _logger.LogError("Lyria API error {Status}: {Body}", (int)response.StatusCode, errorBody);
            throw new InvalidOperationException($"Lyria API returned {(int)response.StatusCode}: {errorBody}");
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct);

        if (!TryExtractAudio(json.RootElement, out var audio, out var contentType))
        {
            // A 200 with no audio payload is how the endpoint reports a filtered generation in
            // some cases; treat it as a refusal rather than throwing, so the caller can retry.
            _logger.LogInformation("Lyria returned 200 with no audio payload — treating as a refusal.");
            var fallbackMsg = json.RootElement.TryGetProperty("error", out var err) ? err.ToString() : "Safety refusal";
            return MusicGenerationResult.FromRefusal(elapsed, fallbackMsg);
        }

        _logger.LogInformation(
            "Lyria track generated in {Elapsed}ms. Size={Size} bytes", elapsed, audio.Length);
        return new MusicGenerationResult(audio, contentType, elapsed);
    }

    /// <summary>
    /// Distinguishes a safety-filter rejection from a real API fault. Google signals filtering with
    /// a 400 carrying a blocked/safety/prohibited marker, so both the status and the body are
    /// checked — a bare 400 (malformed request) must still surface as an error.
    /// </summary>
    private static bool IsSafetyRefusal(HttpStatusCode status, string body)
    {
        if (status != HttpStatusCode.BadRequest) return false;

        return body.Contains("SAFETY", StringComparison.OrdinalIgnoreCase)
            || body.Contains("BLOCKED", StringComparison.OrdinalIgnoreCase)
            || body.Contains("PROHIBITED_CONTENT", StringComparison.OrdinalIgnoreCase)
            || body.Contains("policy", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pulls base64 audio out of the response. The preview contract is not stable, so a few known
    /// shapes are accepted rather than binding hard to one.
    /// </summary>
    private static bool TryExtractAudio(JsonElement root, out byte[] audio, out string contentType)
    {
        audio = [];
        contentType = "audio/mpeg";

        foreach (var candidate in EnumerateAudioNodes(root))
        {
            if (candidate.TryGetProperty("mime_type", out var mime) && mime.ValueKind == JsonValueKind.String)
                contentType = mime.GetString() ?? contentType;
            else if (candidate.TryGetProperty("mimeType", out var mime2) && mime2.ValueKind == JsonValueKind.String)
                contentType = mime2.GetString() ?? contentType;

            foreach (var name in (string[])["data", "audio", "b64_audio", "bytesBase64Encoded"])
            {
                if (!candidate.TryGetProperty(name, out var dataEl) || dataEl.ValueKind != JsonValueKind.String)
                    continue;

                var raw = dataEl.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                try
                {
                    audio = Convert.FromBase64String(raw);
                    return audio.Length > 0;
                }
                catch (FormatException)
                {
                    // Not base64 — keep looking rather than failing the whole response.
                }
            }
        }

        return false;
    }

    private static IEnumerable<JsonElement> EnumerateAudioNodes(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;

            foreach (var prop in root.EnumerateObject())
            {
                foreach (var nested in EnumerateAudioNodes(prop.Value))
                    yield return nested;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                foreach (var nested in EnumerateAudioNodes(item))
                    yield return nested;
            }
        }
    }

    /// <summary>Best-effort extraction of a human-readable message from an error/refusal body.</summary>
    private static string? ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var node in EnumerateAudioNodes(doc.RootElement))
            {
                if (node.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    return msg.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON body — fall through to the truncated raw text.
        }

        return string.IsNullOrWhiteSpace(body) ? null : body[..Math.Min(body.Length, 300)];
    }
}
