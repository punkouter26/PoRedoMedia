using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.AI.Vision.ImageAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>
/// Looks at an image through a chat model that accepts pictures. Serves both the Azure OpenAI
/// vision option and the local Ollama one; they differ only in the client they are given.
/// </summary>
public sealed class ChatVisionService(IChatClient chat) : IVisionService
{
    private const string SystemPrompt =
        "You are an image analyst. Look at the image. description: one vivid, specific sentence "
        + "describing what is happening. tags: 8-14 lowercase single-word or two-word labels for the "
        + "objects, setting and activity present. "
        + "Report only what is visible; do not invent. Do not describe or infer race, ethnicity, "
        + "skin tone, body size or weight, age, disability, or attractiveness.";

    public async Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default)
    {
        // The answer's shape is sent as a JSON schema, so there is nothing to parse by hand.
        var response = await chat.GetResponseAsync<Answer>(
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.User, [new DataContent(image, "image/jpeg"), new TextContent("Analyze this image now.")]),
        ], cancellationToken: ct);

        return response.TryGetResult(out var answer) && !string.IsNullOrWhiteSpace(answer.Description)
            ? new VisionResult(
                answer.Description.Trim(),
                [.. (answer.Tags ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().Take(16)], 1.0)
            : throw new RunStepException("The vision model returned nothing usable for this picture.");
    }

    private sealed record Answer(string Description, string[]? Tags);
}

/// <summary>Google Gemini vision over its REST API.</summary>
public sealed class GeminiVisionService(IConfiguration configuration, IHttpClientFactory http) : IVisionService
{
    public const string HttpClientName = "GeminiApi";

    // A "-latest" alias on purpose: Google retires dated model ids, and a retired id fails every call.
    private readonly string _model = configuration[ConfigKeys.GoogleVisionModel] is { Length: > 0 } model ? model : "gemini-flash-lite-latest";

    public async Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default)
    {
        var body = new
        {
            systemInstruction = new
            {
                role = "system",
                parts = new[]
                {
                    new
                    {
                        text = "You are an expert image analyst. Look at the image and reply in JSON format with schema: "
                             + "{\"description\": \"one vivid, specific sentence describing what is happening\", "
                             + "\"tags\": [\"8-14 lowercase labels for the objects, setting, and activity present\"]}. "
                             + "Report only what is visible; do not invent.",
                    },
                },
            },
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { inlineData = new { mimeType = "image/jpeg", data = Convert.ToBase64String(image) } },
                        new { text = "Analyze this image and return the structured description and tags." },
                    },
                },
            },
            generationConfig = new { responseMimeType = "application/json", temperature = 0.2 },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent");
        request.Headers.Add("x-goog-api-key", configuration[ConfigKeys.GoogleApiKey]);
        request.Content = JsonContent.Create(body);

        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini vision returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return VisionJson.Parse(Text(doc.RootElement));
    }

    /// <summary>The text of the first candidate, or empty when the model returned none.</summary>
    internal static string Text(JsonElement root) =>
        root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
        && candidates[0].TryGetProperty("content", out var content)
        && content.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0
        && parts[0].TryGetProperty("text", out var text)
            ? text.GetString() ?? ""
            : "";
}

/// <summary>
/// Azure Computer Vision. In the region the app's resource lives in, captioning is unavailable,
/// so this usually answers with tags only and says so.
/// </summary>
public sealed class AzureVisionService(IConfiguration configuration) : IVisionService
{
    internal const string TagsOnlyReason =
        "Azure Computer Vision could not caption this picture, so the description was built from detected tags.";

    private readonly ImageAnalysisClient _client = new(
        new Uri(configuration[ConfigKeys.ComputerVisionEndpoint]!), new AzureKeyCredential(configuration[ConfigKeys.ComputerVisionApiKey]!));
    private readonly float _minTagConfidence = configuration.GetValue<float?>(ConfigKeys.ComputerVisionMinTagConfidence) ?? 0.6f;
    private volatile bool _captionUnsupported;

    public async Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default)
    {
        if (!_captionUnsupported)
        {
            try
            {
                var full = (await _client.AnalyzeAsync(
                    BinaryData.FromBytes(image), VisualFeatures.Caption | VisualFeatures.Tags,
                    new ImageAnalysisOptions { Language = "en", GenderNeutralCaption = true }, ct)).Value;
                var tags = Tags(full);
                return string.IsNullOrWhiteSpace(full.Caption?.Text)
                    ? new VisionResult(FromTags(tags), tags, 0, TagsOnlyReason)
                    : new VisionResult(full.Caption.Text, tags, full.Caption.Confidence);
            }
            catch (RequestFailedException ex) when (ex.Status == 400 && ex.Message.Contains("Caption"))
            {
                // Remembered, so later calls do not pay for a request that is known to fail.
                _captionUnsupported = true;
            }
        }

        var tagsOnly = Tags((await _client.AnalyzeAsync(
            BinaryData.FromBytes(image), VisualFeatures.Tags, new ImageAnalysisOptions { Language = "en" }, ct)).Value);
        return new VisionResult(FromTags(tagsOnly), tagsOnly, 0, TagsOnlyReason);
    }

    private IReadOnlyList<string> Tags(ImageAnalysisResult result) =>
        [.. (result.Tags?.Values ?? []).Where(t => t.Confidence >= _minTagConfidence).Select(t => t.Name)];

    private static string FromTags(IReadOnlyList<string> tags) =>
        tags.Count > 0 ? $"A photo showing {string.Join(", ", tags.Take(8))}" : "No description available";
}

/// <summary>Reads the <c>{"description": "...", "tags": [...]}</c> answer the vision prompts ask for.</summary>
public static class VisionJson
{
    public static VisionResult Parse(string text)
    {
        text = text.Trim();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var description = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var tags = doc.RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString()?.Trim().ToLowerInvariant() ?? "").Where(x => x.Length > 0).Distinct().Take(16).ToList()
                : [];
            return new VisionResult(description.Trim(), tags, 1.0);
        }
        catch (JsonException)
        {
            // A model that ignored the JSON instruction still described the picture; keep the words.
            return new VisionResult(text, [], 1.0);
        }
    }
}

/// <summary>
/// Remembers what a provider said about an image, so a re-run on the same picture is free. Each
/// provider gets its own scope: one shared cache would hand one provider's answer to a caller who
/// asked for another.
/// </summary>
public sealed class CachingVisionService(IVisionService inner, HybridCache cache, string scope) : IVisionService
{
    private static readonly HybridCacheEntryOptions Entry = new() { Expiration = TimeSpan.FromHours(6) };

    public async Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default) =>
        await cache.GetOrCreateAsync(
            $"vision:{scope}:{Convert.ToHexString(SHA256.HashData(image))}",
            async token => await inner.AnalyzeAsync(image, token), Entry, cancellationToken: ct);
}

/// <summary>
/// Maps a picked model id to its provider. With no id, prefers Gemini, then Azure OpenAI, then
/// Azure Computer Vision: the first gives real sentences cheaply, the last usually only tags.
/// </summary>
public sealed class VisionServiceRouter(IReadOnlyDictionary<string, IVisionService> providers) : IVisionServiceRouter
{
    private static readonly string[] Preference =
        [AiProviderIds.GeminiVision, AiProviderIds.AzureOpenAiVision, AiProviderIds.AzureComputerVision, AiProviderIds.OllamaVision];

    /// <summary>The provider ids that are configured here, in order of preference.</summary>
    public IReadOnlyList<string> Available => [.. Preference.Where(providers.ContainsKey)];

    public IVisionService Resolve(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
            return Available.Count > 0 ? providers[Available[0]] : throw new RunStepException("No vision model is configured.");

        // A browser model runs on the user's device. Reaching the server with one is a client
        // error, and quietly billing a cloud provider in its place would be a surprise charge.
        return providers.TryGetValue(modelId, out var provider)
            ? provider
            : throw new RunStepException("The picked vision model is not available on this server.");
    }
}

public static class VisionInput
{
    /// <summary>
    /// What the picture shows: the description the user's own device wrote when there is one, and
    /// otherwise the picked provider's. With a device description the server never looks at the
    /// picture, so choosing the free on-device model can never cost a provider call.
    /// </summary>
    public static async Task<VisionResult> SeeAsync(this IVisionServiceRouter router, RunContext context, byte[] image, CancellationToken ct) =>
        UserText.Clean(context.Option(PoRedoMedia.Shared.Models.RunOptions.VisionDescription), 2000) is { } onDevice
            ? new VisionResult(onDevice, [], 1)
            : await router.Resolve(context.Option(PoRedoMedia.Shared.Models.RunOptions.VisionModel)).AnalyzeAsync(image, ct);
}
