using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using PoRedoMedia.Api.Features.PhotoToVideo;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>
/// Gemini watches the whole video, with its sound, and names the moments a meme editor would hit.
/// The clip goes up through the Files API (a video does not fit an inline request), is analysed in
/// one <c>generateContent</c> call, and is deleted again.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> Labelling frames sampled every few seconds misses what happens between them
/// and everything that is only heard. A whole-clip read sees the slip, not the before and after,
/// and hears the crash. It is also one call where frames took several batches.</para>
/// <para><b>Cost.</b> Asked at low media resolution: labels are scene-level, which a low-detail
/// view answers as well as a sharp one, at about a quarter of the tokens.</para>
/// </remarks>
public sealed partial class GeminiClipVisionService(
    IConfiguration configuration, IHttpClientFactory http, StorageClients storage, ILogger<GeminiClipVisionService> logger) : IClipVision
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Clip vision ({Model}): {Count} moment(s) in {ElapsedMs}ms: {Labels}")]
    private partial void LogLabels(string model, int count, long elapsedMs, string labels);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Clip vision could not delete uploaded file {File}; Google removes it after 48 hours")]
    private partial void LogDeleteFailed(Exception ex, string file);

    private const string UploadUrl = "https://generativelanguage.googleapis.com/upload/v1beta/files";
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";
    private const int MaxMoments = 40;
    private const int MaxVocabulary = 250;
    private static readonly TimeSpan ProcessingLimit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    private const string SystemPrompt =
        """
        You are a meme director's eyes and ears. Watch the whole video and listen to its sound.
        List the moments a meme editor would hit with a sound effect, a caption or a sticker:
        visible actions and reactions, cuts, and notable sounds (an impact, a scream, a laugh,
        a music drop, an awkward silence). Cover the whole clip, at least one moment for every
        few seconds of footage, a quiet stretch included ("static room", "person talking").
        For each moment:
        - seconds: when it happens, in seconds from the start of the video.
        - label: what happens, at most 8 words (e.g. "man slips on wet floor", "crowd gasps").
        - energy: 0 (nothing happens) to 1 (chaos, impact, peak reaction).
        - tags: 0 to 4 tags copied exactly from the TAG VOCABULARY that fit the moment.
        - subjectX, subjectY: the main subject's centre (a face if there is one) as 0..1 of frame width and height, or null.
        Report only what is in the video. Never describe race, ethnicity, skin tone, body size, age or disability.
        """;

    private static readonly object Schema = new
    {
        type = "OBJECT",
        required = new[] { "moments" },
        properties = new
        {
            moments = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    required = new[] { "seconds", "label", "energy", "tags" },
                    properties = new
                    {
                        seconds = new { type = "NUMBER" },
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

    private readonly string _model = configuration[ConfigKeys.GoogleClipVisionModel] is { Length: > 0 } clip ? clip
        : configuration[ConfigKeys.GoogleVisionModel] is { Length: > 0 } vision ? vision : "gemini-flash-lite-latest";

    public async Task<SceneLabel[]> AnalyseAsync(MediaItem video, IReadOnlyCollection<string> tagVocabulary, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var file = await UploadAsync(video, cancellationToken);
        try
        {
            var uri = await WaitUntilActiveAsync(file, cancellationToken);
            var vocabulary = tagVocabulary.Take(MaxVocabulary).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var text = await DescribeAsync(uri, video.ContentType, vocabulary, cancellationToken);
            var labels = Parse(text, vocabulary, video.DurationSeconds);
            LogLabels(_model, labels.Length, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                string.Join(", ", labels.Select(l => string.Create(CultureInfo.InvariantCulture, $"t={l.TimestampSeconds:F1}s→{l.Label}"))));
            return labels;
        }
        finally
        {
            await DeleteAsync(file);
        }
    }

    /// <summary>The model's moments as labels in time order; times outside the clip are dropped.</summary>
    internal static SceneLabel[] Parse(string text, IReadOnlySet<string> vocabulary, double? durationSeconds)
    {
        using var doc = JsonDocument.Parse(AiFoundryClient.StripCodeFence(text));
        if (!doc.RootElement.TryGetProperty("moments", out var moments) || moments.ValueKind != JsonValueKind.Array)
            return [];

        var end = durationSeconds is > 0 and var d ? d : double.MaxValue;
        var labels = new List<SceneLabel>();
        foreach (var m in moments.EnumerateArray())
        {
            if (Number(m, "seconds") is not { } at || at < 0 || at >= end
                || !m.TryGetProperty("label", out var l) || l.GetString()?.Trim() is not { Length: > 0 } label)
            {
                continue;
            }

            labels.Add(new SceneLabel(Math.Round(at, 2), label)
            {
                Energy = Number(m, "energy") is { } e ? Math.Clamp(e, 0, 1) : 0.5,
                Tags = m.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? [.. tags.EnumerateArray().Select(t => t.GetString() ?? "").Where(vocabulary.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Take(4)]
                    : [],
                SubjectX = Number(m, "subjectX") is { } x ? Math.Clamp(x, 0, 1) : null,
                SubjectY = Number(m, "subjectY") is { } y ? Math.Clamp(y, 0, 1) : null,
            });
        }

        return [.. labels.DistinctBy(l => l.TimestampSeconds).OrderBy(l => l.TimestampSeconds).Take(MaxMoments)];

        static double? Number(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    }

    /// <summary>Sends the video to the Files API by the resumable protocol; returns the file's name (<c>files/…</c>).</summary>
    private async Task<string> UploadAsync(MediaItem video, CancellationToken ct)
    {
        // The Veo client: a long timeout and no retries, because an upload stream cannot be sent twice.
        var client = http.CreateClient(VeoVideoService.HttpClientName);
        var blob = storage.Blob(video.SourcePath);
        var size = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength;

        using var start = new HttpRequestMessage(HttpMethod.Post, UploadUrl)
        {
            Content = JsonContent.Create(new { file = new { display_name = video.Id.ToString() } }),
        };
        start.Headers.Add("x-goog-api-key", configuration[ConfigKeys.GoogleApiKey]);
        start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        start.Headers.Add("X-Goog-Upload-Command", "start");
        start.Headers.Add("X-Goog-Upload-Header-Content-Length", size.ToString(CultureInfo.InvariantCulture));
        start.Headers.Add("X-Goog-Upload-Header-Content-Type", video.ContentType);
        using var started = await client.SendAsync(start, ct);
        await EnsureAsync(started, "start the upload", ct);
        var sessionUrl = started.Headers.TryGetValues("X-Goog-Upload-URL", out var urls) ? urls.FirstOrDefault() : null;
        if (string.IsNullOrEmpty(sessionUrl))
            throw new InvalidOperationException("Gemini Files API gave no upload URL.");

        await using var content = await blob.OpenReadAsync(cancellationToken: ct);
        using var upload = new HttpRequestMessage(HttpMethod.Post, sessionUrl) { Content = new StreamContent(content) };
        upload.Content.Headers.ContentLength = size;
        upload.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(video.ContentType);
        upload.Headers.Add("X-Goog-Upload-Offset", "0");
        upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        using var uploaded = await client.SendAsync(upload, ct);
        await EnsureAsync(uploaded, "upload the video", ct);

        using var json = await JsonDocument.ParseAsync(await uploaded.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.GetProperty("file").GetProperty("name").GetString()
            ?? throw new InvalidOperationException("Gemini Files API returned no file name.");
    }

    /// <summary>A video is processed after upload; it can be used once it is ACTIVE. Returns its URI.</summary>
    private async Task<string> WaitUntilActiveAsync(string file, CancellationToken ct)
    {
        var client = http.CreateClient(VeoVideoService.HttpClientName);
        var deadline = DateTime.UtcNow + ProcessingLimit;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + file);
            request.Headers.Add("x-goog-api-key", configuration[ConfigKeys.GoogleApiKey]);
            using var response = await client.SendAsync(request, ct);
            await EnsureAsync(response, "check the upload", ct);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var state = json.RootElement.TryGetProperty("state", out var s) ? s.GetString() : null;
            if (state == "ACTIVE")
                return json.RootElement.GetProperty("uri").GetString()!;
            if (state == "FAILED")
                throw new InvalidOperationException("Gemini could not process the video.");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Gemini took too long to process the video.");
            await Task.Delay(PollEvery, ct);
        }
    }

    private async Task<string> DescribeAsync(string fileUri, string mimeType, HashSet<string> vocabulary, CancellationToken ct)
    {
        var system = vocabulary.Count > 0 ? $"{SystemPrompt}\nTAG VOCABULARY: {string.Join(", ", vocabulary)}" : SystemPrompt;
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { fileData = new { mimeType, fileUri } },
                        new { text = "Watch and listen to this video, then return its moments." },
                    },
                },
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = Schema,
                temperature = 0.2,
                mediaResolution = "MEDIA_RESOLUTION_LOW",
            },
        };

        // The Gemini client: retried on a transient failure, and metered.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}models/{_model}:generateContent") { Content = JsonContent.Create(body) };
        request.Headers.Add("x-goog-api-key", configuration[ConfigKeys.GoogleApiKey]);
        using var response = await http.CreateClient(GeminiVisionService.HttpClientName).SendAsync(request, ct);
        await EnsureAsync(response, "watch the video", ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return GeminiVisionService.Text(json.RootElement);
    }

    private async Task DeleteAsync(string file)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, BaseUrl + file);
            request.Headers.Add("x-goog-api-key", configuration[ConfigKeys.GoogleApiKey]);
            using var response = await http.CreateClient(VeoVideoService.HttpClientName).SendAsync(request, CancellationToken.None);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            LogDeleteFailed(ex, file);
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini could not {what} ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
    }
}
