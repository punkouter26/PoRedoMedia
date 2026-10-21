using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>Looks at an image through a chat model that accepts pictures (Azure OpenAI).</summary>
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

    public async Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default)
    {
        // The cache runs the factory outside this flow; the call still belongs to this run's bill.
        var tally = AiMeter.Tally;
        return await cache.GetOrCreateAsync(
            $"vision:{scope}:{Convert.ToHexString(SHA256.HashData(image))}",
            async token =>
            {
                AiMeter.Join(tally);
                return await inner.AnalyzeAsync(image, token);
            },
            Entry, cancellationToken: ct);
    }
}

public static class VisionInput
{
    /// <summary>
    /// What the picture shows: the description the user's own device wrote when there is one, and
    /// otherwise the configured provider's. With a device description the server never looks at the
    /// picture, so the free on-device model can never cost a provider call.
    /// </summary>
    /// <remarks>
    /// With <paramref name="reads"/>, the provider's answer about the current picture is kept with it,
    /// so another run on the same picture asks nothing. A degraded answer (tags in place of a
    /// caption) is not kept, so the next run tries again.
    /// </remarks>
    public static async Task<VisionResult> SeeAsync(
        this IVisionService vision, RunContext context, byte[] image, CancellationToken ct, AiReadStore? reads = null)
    {
        if (UserText.Clean(context.Option(PoRedoMedia.Shared.Models.RunOptions.VisionDescription), 2000) is { } onDevice)
            return new VisionResult(onDevice, [], 1);

        // An on-device model with no description is a client error, and quietly billing a cloud
        // provider in its place would be a surprise charge.
        if (AiCatalog.RunsInBrowser(context.Option(PoRedoMedia.Shared.Models.RunOptions.VisionModel)))
            throw new RunStepException("The on-device description did not arrive. Try the run again.");

        return reads is null
            ? await vision.AnalyzeAsync(image, ct)
            : await reads.GetOrReadAsync(
                context.Current.Id, "vision-default", () => vision.AnalyzeAsync(image, ct),
                seen => seen.FallbackReason is null && seen.Description.Length > 0, ct);
    }
}
