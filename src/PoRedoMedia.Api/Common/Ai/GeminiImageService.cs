using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>Gemini image generation over its REST API: from words alone, redrawing a given picture, or redrawing it larger.</summary>
public sealed class GeminiImageService(IConfiguration configuration, IHttpClientFactory http) : IImageGenerationService, IImageUpscaler
{
    private static readonly JsonSerializerOptions OmitNulls = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly (string Label, double Value)[] SupportedRatios =
        [("1:1", 1.0), ("2:3", 2 / 3.0), ("3:2", 1.5), ("3:4", 0.75), ("4:3", 4 / 3.0),
         ("4:5", 0.8), ("5:4", 1.25), ("9:16", 9 / 16.0), ("16:9", 16 / 9.0), ("21:9", 21 / 9.0)];

    // Small variations asked for by seed, so "another take" really differs.
    private static readonly string[] SeedCues =
    [
        "with a slightly cooler color temperature", "with a slightly warmer color temperature",
        "with more pronounced shadows and contrast", "with softer lighting and pastel tones",
        "with a more saturated, vivid look", "with a muted, desaturated film-stock feel",
        "with a different camera lens perspective", "with a subtle stylistic reinterpretation",
    ];

    private readonly string _model = configuration[ConfigKeys.GoogleImageModel] is { Length: > 0 } model ? model : "gemini-2.5-flash-image";

    // The flash model draws at about 1K only; the pro model takes an output size.
    private readonly string _upscaleModel = configuration[ConfigKeys.GoogleUpscaleModel] is { Length: > 0 } upscale ? upscale : "gemini-3-pro-image";

    public Task<GeneratedImage> GenerateAsync(string prompt, byte[]? matchAspectOf = null, CancellationToken ct = default) =>
        SendAsync(prompt, reference: null, AspectRatioOf(matchAspectOf), ct);

    public Task<GeneratedImage> EditAsync(string prompt, byte[] image, int seed = 0, CancellationToken ct = default) =>
        SendAsync(
            "You are a creative image editor. Preserve the person's facial features exactly. Apply: " + prompt
                + (seed == 0 ? "" : $". Render the result {SeedCues[seed % SeedCues.Length]}."),
            image, aspectRatio: null, ct);

    public Task<GeneratedImage> UpscaleAsync(byte[] image, string size, CancellationToken ct = default) =>
        SendAsync(
            "Reproduce this exact image at a higher resolution. Keep the composition, every subject, "
                + "every face, all text and all colours exactly as they are. Add only fine, natural detail and "
                + "sharpness. Do not restyle, crop, reframe, add or remove anything.",
            image, AspectRatioOf(image), ct, _upscaleModel, size == "4K" ? "4K" : "2K");

    /// <summary>The supported aspect ratio nearest to the image's own, or null when it cannot be read.</summary>
    internal static string? AspectRatioOf(byte[]? image)
    {
        if (image is null || ImageBytes.Identify(image) is not { } size)
            return null;
        var ratio = (double)size.Width / size.Height;
        return SupportedRatios.MinBy(r => Math.Abs(Math.Log(r.Value / ratio))).Label;
    }

    private async Task<GeneratedImage> SendAsync(
        string prompt, byte[]? reference, string? aspectRatio, CancellationToken ct, string? model = null, string? imageSize = null)
    {
        var parts = new List<object>();
        if (reference is not null)
            parts.Add(new { inlineData = new { mimeType = "image/jpeg", data = Convert.ToBase64String(reference) } });
        parts.Add(new { text = prompt });

        var body = new
        {
            // No system instruction: with one, gemini-2.5-flash-image answers NO_IMAGE and draws
            // nothing (verified 2026-10-05). When it will not draw, it says why in a text part.
            contents = new[] { new { parts } },
            generationConfig = new
            {
                responseModalities = new[] { "image" },
                temperature = 1.0,
                imageConfig = aspectRatio is null && imageSize is null ? null : new { aspectRatio, imageSize },
            },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model ?? _model}:generateContent");
        request.Headers.Add("x-goog-api-key", configuration[ConfigKeys.GoogleApiKey]);
        request.Content = JsonContent.Create(body, options: OmitNulls);

        using var response = await http.CreateClient(GeminiVisionService.HttpClientName).SendAsync(request, ct);
        if ((int)response.StatusCode == 429)
            throw new RunStepException("The image model is busy right now. Wait a minute and try again.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini image returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Read(json.RootElement);
    }

    /// <summary>The image in a response, or a failure the user can read when the model declined.</summary>
    internal static GeneratedImage Read(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates))
            throw new RunStepException("The image model declined this request.");

        var finishReasons = new List<string>();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.TryGetProperty("finishReason", out var finish))
                finishReasons.Add(finish.GetString() ?? "");

            if (candidate.TryGetProperty("finishReason", out var reason)
                && reason.GetString() is "SAFETY" or "RECITATION" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" or "IMAGE_SAFETY")
            {
                throw new RunStepException("The image model's safety filter blocked this picture.");
            }

            if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
                continue;

            string? said = null;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("inlineData", out var inline))
                    return new GeneratedImage(
                        Convert.FromBase64String(inline.GetProperty("data").GetString()!),
                        inline.TryGetProperty("mimeType", out var mime) ? mime.GetString() ?? "image/png" : "image/png");
                if (part.TryGetProperty("text", out var text))
                    said = text.GetString();
            }

            if (said is not null)
            {
                const string prefix = "REFUSE:";
                var why = said.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? said[prefix.Length..].Trim() : said.Trim();
                throw new RunStepException($"The image model declined: {why}");
            }
        }

        throw new RunStepException($"The image model returned no picture ({string.Join(", ", finishReasons)}). Try again.");
    }
}
