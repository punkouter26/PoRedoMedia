using System.Globalization;
using System.Text.Json;
using PoRedoMedia.Api.Common.Ai;

namespace PoRedoMedia.Api.Features.Memeify;

/// <summary>
/// Labels sampled video frames with Gemini: the same prompt, answer shape and parsing as
/// <see cref="AiFoundryVisionService"/>, so the two are interchangeable (<c>Vision:FrameProvider</c>).
/// </summary>
/// <remarks>
/// Every frame goes in one request, each after its own number and time, at low media resolution:
/// labels are scene-level, and Gemini takes forty small images in a call where the chat
/// deployment needed batches of eight. It is what gives Meme-ify eyes on a server that has a
/// Google key and no Foundry resource.
/// </remarks>
public sealed partial class GeminiFrameVisionService(IConfiguration configuration, IHttpClientFactory http, ILogger<GeminiFrameVisionService> logger)
    : IAiVisionService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Frame vision ({Model}): {Count} label(s) from {Frames} frame(s)")]
    private partial void LogLabels(string model, int count, int frames);

    private static readonly object Schema = new
    {
        type = "OBJECT",
        required = new[] { "frames" },
        properties = new
        {
            frames = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    required = new[] { "frame", "label", "energy", "tags" },
                    properties = new
                    {
                        frame = new { type = "INTEGER" },
                        label = new { type = "STRING" },
                        energy = new { type = "NUMBER" },
                        tags = new { type = "ARRAY", items = new { type = "STRING" } },
                        subjectX = new { type = "NUMBER", nullable = true },
                        subjectY = new { type = "NUMBER", nullable = true },
                    },
                },
            },
        },
    };

    private readonly string _model = configuration[ConfigKeys.GoogleVisionModel] is { Length: > 0 } model ? model : "gemini-flash-lite-latest";

    public async Task<SceneLabel[]> AnalyseAsync(
        IReadOnlyList<KeyFrame> frames, IReadOnlyCollection<string> tagVocabulary, CancellationToken cancellationToken = default)
    {
        if (frames.Count == 0)
            return [];

        var vocabulary = tagVocabulary.Take(AiFoundryVisionService.MaxVocabulary).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var system = vocabulary.Count > 0
            ? $"{AiFoundryVisionService.SystemPrompt}\nTAG VOCABULARY: {string.Join(", ", vocabulary)}"
            : AiFoundryVisionService.SystemPrompt;

        var parts = new List<object>(frames.Count * 2 + 1);
        for (var i = 0; i < frames.Count; i++)
        {
            parts.Add(new { text = string.Create(CultureInfo.InvariantCulture, $"frame {i} (t={frames[i].TimestampSeconds:0.0}s)") });
            parts.Add(new { inlineData = new { mimeType = frames[i].MediaType, data = Convert.ToBase64String(frames[i].Image.Span) } });
        }
        parts.Add(new { text = "Return one entry for every frame." });

        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { parts } },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = Schema,
                temperature = 0.2,
                mediaResolution = "MEDIA_RESOLUTION_LOW",
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("x-goog-api-key", configuration[ConfigKeys.GoogleApiKey]);
        using var response = await http.CreateClient(GeminiVisionService.HttpClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini frame vision returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var labels = AiFoundryVisionService.Parse(GeminiVisionService.Text(json.RootElement), frames, vocabulary);
        LogLabels(_model, labels.Length, frames.Count);
        return labels;
    }
}
