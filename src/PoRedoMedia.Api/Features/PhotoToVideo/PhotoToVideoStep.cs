using System.Text.Json;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.PhotoToVideo;

/// <summary>Turns the current image into a short clip with sound, following the user's description.</summary>
public sealed class PhotoToVideoStep(
    IVideoGenerationService video, BlobStorageService blobs, MediaOutputs outputs, FFmpegProcess ffmpeg) : IRunStep
{
    public IReadOnlySet<MediaFunction> Handles { get; } = new HashSet<MediaFunction> { MediaFunction.PhotoToVideo };

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var prompt = context.Option(RunOptions.VideoPrompt)?.Trim();
        if (prompt is null || prompt.Length is < 3 or > 1200)
            throw new RunStepException("Describe what should happen in the video, in 3 to 1,200 characters.");

        var image = ImageBytes.ForProcessing(await blobs.ReadAllBytesAsync(context.Current.SourcePath, ct));
        await context.ReportAsync("Making the video. This takes a few minutes.");
        var clip = await video.GenerateAsync(image, prompt, ct);

        var item = await outputs.SaveAsync(
            context.Current, MediaFunction.PhotoToVideo, MediaKind.Video, clip, "video/mp4", ".mp4", ct,
            durationSeconds: await DurationAsync(clip, ct));
        await context.AddOutputAsync(item);
    }

    private async Task<double?> DurationAsync(byte[] clip, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"poredomedia-clip-{Guid.NewGuid():N}.mp4");
        try
        {
            await File.WriteAllBytesAsync(path, clip, ct);
            return await ffmpeg.DurationSecondsAsync(path, ct) is > 0 and var seconds ? seconds : null;
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Google Veo: start a long-running job, wait for it, download the clip.</summary>
public sealed class VeoVideoService(IConfiguration configuration, IHttpClientFactory http, ILogger<VeoVideoService> logger) : IVideoGenerationService
{
    public const string HttpClientName = "Veo";
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";
    private const string AudioDirective = " Include ambient sound and natural audio that fits the scene.";

    /// <summary>A healthy job takes one to five minutes. Past this the run fails and can be retried.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(6);

    private readonly string _model = configuration[ConfigKeys.GoogleVeoModel] is { Length: > 0 } model ? model : "veo-3.1-lite-generate-preview";

    public async Task<byte[]> GenerateAsync(byte[] image, string prompt, CancellationToken ct = default)
    {
        var client = http.CreateClient(HttpClientName);
        var body = new
        {
            instances = new[]
            {
                new { prompt = WithAudioDirection(prompt), image = new { bytesBase64Encoded = Convert.ToBase64String(image), mimeType = "image/jpeg" } },
            },
            parameters = new { sampleCount = 1 },
        };

        using var start = await client.SendAsync(Request(HttpMethod.Post, $"models/{_model}:predictLongRunning", JsonContent.Create(body)), ct);
        if (!start.IsSuccessStatusCode)
            throw Rejected(await start.Content.ReadAsStringAsync(ct), (int)start.StatusCode);

        using var started = await JsonDocument.ParseAsync(await start.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var operation = started.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        if (string.IsNullOrEmpty(operation))
            throw new RunStepException("The video service did not start the job. Try again.");

        // ponytail: the job is waited for in this process. A restart mid-wait loses it and the run
        // is marked interrupted; persist the operation name and resume if that becomes common.
        var deadline = DateTime.UtcNow + Limit;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollEvery, ct);
            using var poll = await client.SendAsync(Request(HttpMethod.Get, operation), ct);
            if (!poll.IsSuccessStatusCode)
            {
                logger.LogWarning("Veo poll returned {Status}; retrying", (int)poll.StatusCode);
                continue;
            }

            using var status = await JsonDocument.ParseAsync(await poll.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = status.RootElement;
            if (!root.TryGetProperty("done", out var done) || !done.GetBoolean())
                continue;
            if (root.TryGetProperty("error", out var error))
                throw new RunStepException("The video service rejected this: " + (error.TryGetProperty("message", out var m) ? m.GetString() : "no reason given"));
            if (!TryExtractVideoUri(root, out var uri))
                throw new RunStepException("The video service finished without a clip, which usually means its safety filter blocked it. Try a different description.");

            using var download = await client.SendAsync(Request(HttpMethod.Get, uri), ct);
            download.EnsureSuccessStatusCode();
            return await download.Content.ReadAsByteArrayAsync(ct);
        }

        throw new RunStepException("The video took too long and was abandoned. Try again.");
    }

    private HttpRequestMessage Request(HttpMethod method, string url, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : BaseUrl + url) { Content = content };
        request.Headers.Add("x-goog-api-key", configuration[ConfigKeys.GoogleApiKey]);
        return request;
    }

    private static RunStepException Rejected(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
                return new RunStepException($"The video service refused the request: {message.GetString()}");
        }
        catch (JsonException)
        {
            // Not JSON; fall through.
        }

        return new RunStepException($"The video service refused the request ({status}).");
    }

    /// <summary>
    /// Veo makes a silent clip unless sound is asked for. A prompt that already talks about sound,
    /// or asks for silence, is left alone.
    /// </summary>
    public static string WithAudioDirection(string prompt)
    {
        string[] audioCues =
        [
            "sound", "audio", "music", "voice", "narrat", "speaks", "speech", "dialog", "talking", "speak",
            "hear", "listen", "laugh", "quiet", "silent", "silence", "mute",
        ];
        return audioCues.Any(cue => prompt.Contains(cue, StringComparison.OrdinalIgnoreCase)) ? prompt : prompt.TrimEnd() + AudioDirective;
    }

    internal static bool TryExtractVideoUri(JsonElement root, out string uri)
    {
        uri = "";
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("generateVideoResponse", out var generated)
            && generated.TryGetProperty("generatedSamples", out var samples)
            && samples.ValueKind == JsonValueKind.Array && samples.GetArrayLength() > 0
            && samples[0].TryGetProperty("video", out var clip)
            && clip.TryGetProperty("uri", out var value) && value.ValueKind == JsonValueKind.String)
        {
            uri = value.GetString()!;
            return true;
        }

        return false;
    }
}

/// <summary>Stands in for Veo with a real, playable two-second clip of the picture, made locally.</summary>
public sealed class MockVideoService(FFmpegProcess ffmpeg) : IVideoGenerationService
{
    public async Task<byte[]> GenerateAsync(byte[] image, string prompt, CancellationToken ct = default)
    {
        var stem = Path.Combine(Path.GetTempPath(), $"poredomedia-mockvideo-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(stem + ".jpg", image, ct);
            var exit = await ffmpeg.RunAsync(
                $"-y -loop 1 -i \"{stem}.jpg\" -f lavfi -i anullsrc=r=44100:cl=stereo -t 2 -r 10 -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" -pix_fmt yuv420p -shortest \"{stem}.mp4\"",
                "mock video", ct);
            return exit == 0 ? await File.ReadAllBytesAsync(stem + ".mp4", ct) : throw new RunStepException("The mock video could not be made. Is ffmpeg installed?");
        }
        finally
        {
            File.Delete(stem + ".jpg");
            File.Delete(stem + ".mp4");
        }
    }
}
