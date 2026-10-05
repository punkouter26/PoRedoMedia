using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security;
using System.Text;
using System.Text.Json;
using OpenAI.Chat;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.VideoRoast;

/// <summary>
/// The roast voiceover: a stand-up comedian insulting what happens in the video, one joke per
/// moment, at most <see cref="SessionRoast.MaxTotalMs"/> of speech in all.
/// </summary>
/// <remarks>
/// Three steps: the chat deployment writes the jokes from the vision labels and the transcript the
/// engine already has (no second look at the frames); a voice engine performs each one; the clips
/// are placed on the timeline so none overlaps and none runs past the end. Every failure belongs
/// to the caller, which renders the video without a roast.
/// <para>
/// The voices are plain HTTP calls, not <see cref="AiFoundryClient"/>'s SDK client: the SDK's API
/// version answers 404 for a <c>gpt-4o-mini-tts</c> deployment, and Azure Speech and Lyria are not
/// OpenAI endpoints at all. <see cref="SessionRoast.Voices"/> offers none while AI calls are
/// intercepted, so a test host never reaches them.
/// </para>
/// </remarks>
public sealed partial class RoastService : IVideoRoast
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "AI usage: Stage=roast-voice Voice={Voice} Session={AiSessionId} Clips={Clips} Characters={Characters} ElapsedMs={ElapsedMs}")]
    private partial void LogUsage(string voice, MediaId aiSessionId, int clips, int characters, long elapsedMs);

    /// <summary>The API version that serves <c>audio/speech</c> for gpt-4o-mini-tts with <c>instructions</c>.</summary>
    private const string SpeechApiVersion = "2025-03-01-preview";

    private const string LyriaUrl = "https://generativelanguage.googleapis.com/v1beta/interactions";

    internal const int MaxJokes = 5;

    /// <summary>Longest joke, in characters. The comic takes about seven seconds over fourteen words.</summary>
    private const int MaxJokeLength = 160;

    /// <summary>Breath between two jokes that would otherwise run into each other.</summary>
    private const int GapMs = 250;

    private const int MaxMoments = 40;

    private const string Schema = """
        {"type":"object","properties":{"jokes":{"type":"array","items":{"type":"object","properties":{"moment":{"type":"integer"},"text":{"type":"string"}},"required":["moment","text"],"additionalProperties":false}}},"required":["jokes"],"additionalProperties":false}
        """;

    private const string SystemPrompt = """
        You are a savage stand-up comedian doing a roast of a video clip. You get a timeline of its moments: what the camera saw and what was said.
        Write short insult jokes, each aimed at ONE moment, to be spoken over the video at that moment.

        Style: brutal, vulgar and profane. This is an adult roast: every joke swears (fuck, shit, ass, damn, hell) and a tame joke is a failure. Specific beats generic: name what is actually happening in that moment and tear it apart.
        Target: what people DO in the clip — their choices, their skill, their timing, their taste, the situation they got themselves into.
        Never: slurs; jokes about race, ethnicity, religion, gender, sexuality, disability or body size; sexual remarks about a person; threats or wishing harm. If a moment shows a child, roast the situation or the adults, not the child.

        Each joke: one or two sentences, at most 14 words, written to be said aloud. No emoji, no hashtags, no stage directions, no quotation marks.
        Spread the jokes across the clip, in time order, at most one per moment. Skip moments with nothing to mock.
        Answer {"jokes":[{"moment": <index of the moment>, "text": "<the joke>"}]}.
        """;

    private const string ComicDelivery =
        "A brash stand-up comedian mid-set at a roast: loud, fast, incredulous, mocking. Land hard on the last words of the line.";

    private const string RapStyle =
        "Aggressive old-school boom-bap diss track. One male rapper, vocals loud and clear up front, hard drums. About 25 seconds.";

    // One client for the service's lifetime; each call brings its own timeout.
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly AiFoundryClient _ai;
    private readonly IConfiguration _config;
    private readonly BlobStorageService _blobs;
    private readonly ILogger<RoastService> _logger;

    public RoastService(AiFoundryClient ai, IConfiguration config, BlobStorageService blobs, ILogger<RoastService> logger)
    {
        _ai = ai;
        _config = config;
        _blobs = blobs;
        _logger = logger;
    }

    /// <summary>
    /// Writes, voices and stores the session's roast. Null when the clip gave nothing to roast or
    /// no joke fitted. <paramref name="labels"/> and <paramref name="transcript"/> are in output time.
    /// </summary>
    public async Task<RoastTrack?> GenerateAsync(
        MediaId mediaId,
        string voice,
        IReadOnlyList<SceneLabel> labels,
        IReadOnlyList<TranscriptSegmentDto> transcript,
        double durationSeconds,
        CancellationToken ct)
    {
        var moments = Moments(labels, transcript);
        if (moments.Count == 0)
            return null;

        var jokes = await WriteJokesAsync(mediaId, moments, durationSeconds, ct);
        if (jokes.Count == 0)
            return null;

        var id = mediaId.Value;
        var sw = Stopwatch.StartNew();
        RoastTrack track;
        if (voice == SessionRoast.Rap)
        {
            // Lyria performs lyrics as one song, so the rap is a single clip from the top.
            var (audio, extension) = await SingAsync(jokes.Select(j => j.Text), ct);
            var path = SessionBlobPaths.RoastClip(id, 0, extension);
            await UploadAsync(path, audio, extension, ct);
            var text = string.Join(" / ", jokes.Select(j => j.Text));
            track = new RoastTrack(voice, path, [new RoastLine(0, AudioDuration.EstimateMs(audio, path), text, path)]);
        }
        else
        {
            // One at a time: a handful of short calls, and a burst would only meet the rate limit.
            // Speaking stops once the clips already fill the roast, so at most one goes unused.
            var videoMs = (long)(durationSeconds * 1000);
            var clips = new List<byte[]>();
            long spokenMs = 0;
            foreach (var joke in jokes.TakeWhile(_ => spokenMs < Math.Min(SessionRoast.MaxTotalMs, videoMs)))
            {
                clips.Add(await SpeakAsync(voice, joke.Text, ct));
                spokenMs += AudioDuration.EstimateMs(clips[^1], ".mp3");
            }

            var placed = Schedule(
                [.. clips.Select((clip, i) => (jokes[i].AtMs, AudioDuration.EstimateMs(clip, ".mp3")))],
                videoMs);
            if (placed.Count == 0)
                return null;

            var lines = new List<RoastLine>();
            using var whole = new MemoryStream();
            foreach (var (index, startMs) in placed)
            {
                var path = SessionBlobPaths.RoastClip(id, lines.Count, "mp3");
                await UploadAsync(path, clips[index], "mp3", ct);
                lines.Add(new RoastLine(startMs, AudioDuration.EstimateMs(clips[index], ".mp3"), jokes[index].Text, path));
                whole.Write(clips[index]);
            }

            // ponytail: the sound file is the clips' bytes back to back. MP3 frames stand alone and
            // every clip comes from one engine at one bitrate, so players read it as one file; an
            // ffmpeg concat (through IMediaToolkit) is the upgrade if a player ever disagrees.
            var audioPath = SessionBlobPaths.RoastAudio(id);
            await UploadAsync(audioPath, whole.ToArray(), "mp3", ct);
            track = new RoastTrack(voice, audioPath, lines);
        }

        LogUsage(voice, mediaId, track.Lines.Count, jokes.Sum(j => j.Text.Length), sw.ElapsedMilliseconds);
        await SessionRoast.SaveAsync(_blobs, mediaId, track, ct);
        return track;
    }

    /// <summary>What the comedian gets to work with: each vision label, and each line of speech at its end.</summary>
    internal static List<(double AtSeconds, string What)> Moments(IReadOnlyList<SceneLabel> labels, IReadOnlyList<TranscriptSegmentDto> transcript)
        => [.. labels
            .Where(l => !string.IsNullOrWhiteSpace(l.Label) && l.Label != "unknown")
            .Select(l => (l.TimestampSeconds, $"seen: {l.Label}"))
            // After the line, so the joke answers it instead of talking over it.
            .Concat(transcript.Select(s => (s.EndSeconds, $"said: \"{s.Text.Replace('"', '\'')}\"")))
            .OrderBy(m => m.Item1)
            .Take(MaxMoments)];

    private async Task<List<(long AtMs, string Text)>> WriteJokesAsync(
        MediaId mediaId, List<(double AtSeconds, string What)> moments, double durationSeconds, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        // A spoken joke runs about seven seconds; more than one per seven would not fit.
        var max = Math.Clamp((int)(durationSeconds / 7), 1, MaxJokes);
        var prompt = new StringBuilder()
            .Append(inv, $"The clip is {durationSeconds:0.#} seconds long. Write {max} joke(s), fewer only when there are fewer moments.\n\nMoments:\n");
        for (var i = 0; i < moments.Count; i++)
            prompt.Append(inv, $"[{i}] t={moments[i].AtSeconds:0.0}s {moments[i].What}\n");

        var raw = await _ai.CompleteJsonAsync(
            new AiCall("roast", _ai.Deployment, "roast", Schema)
            {
                MediaId = mediaId,
                Temperature = 1.0f,
                MaxOutputTokens = 2_500,
            },
            [new SystemChatMessage(SystemPrompt), new UserChatMessage(prompt.ToString())],
            ct);
        return ReadJokes(raw, moments, max);
    }

    /// <summary>The jokes in the model's answer, each at its moment's time: one per moment, in time order.</summary>
    internal static List<(long AtMs, string Text)> ReadJokes(string raw, IReadOnlyList<(double AtSeconds, string What)> moments, int max)
    {
        using var doc = JsonDocument.Parse(AiFoundryClient.StripCodeFence(raw));
        var array = doc.RootElement.ValueKind == JsonValueKind.Object
            ? doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array).Value
            : doc.RootElement;
        if (array.ValueKind != JsonValueKind.Array)
            return [];

        var byMoment = new SortedDictionary<double, string>();
        foreach (var joke in array.EnumerateArray())
        {
            if (joke.ValueKind != JsonValueKind.Object
                || !joke.TryGetProperty("moment", out var moment) || !moment.TryGetInt32(out var index)
                || index < 0 || index >= moments.Count
                || !joke.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
                || UserText.Clean(text.GetString(), MaxJokeLength) is not { } clean)
            {
                continue;
            }

            byMoment.TryAdd(moments[index].AtSeconds, clean);
        }

        return [.. byMoment.Take(max).Select(j => ((long)Math.Round(j.Key * 1000), j.Value))];
    }

    /// <summary>
    /// Where each clip plays: at its moment, pushed later while the previous joke is still being
    /// said, pulled earlier when it would run past the end. A clip that fits neither way, or would
    /// take the roast past <see cref="SessionRoast.MaxTotalMs"/>, is left out.
    /// </summary>
    /// <param name="clips">In time order.</param>
    internal static List<(int Index, long StartMs)> Schedule(IReadOnlyList<(long AtMs, int DurationMs)> clips, long videoMs)
    {
        var placed = new List<(int, long)>();
        long cursor = 0;
        var total = 0;
        for (var i = 0; i < clips.Count; i++)
        {
            var (at, duration) = clips[i];
            var start = Math.Min(Math.Max(at, cursor), videoMs - duration);
            if (duration <= 0 || start < cursor || total + duration > SessionRoast.MaxTotalMs)
                continue;

            placed.Add((i, start));
            cursor = start + duration + GapMs;
            total += duration;
        }

        return placed;
    }

    private Task<byte[]> SpeakAsync(string voice, string text, CancellationToken ct)
    {
        var endpoint = AiFoundryClient.Setting(_config, "AiFoundry:Endpoint")!.TrimEnd('/');
        var key = AiFoundryClient.Setting(_config, "AiFoundry:Key");
        HttpRequestMessage request;
        if (voice == SessionRoast.Comic)
        {
            var deployment = AiFoundryClient.Setting(_config, "AiFoundry:TtsDeployment");
            request = new(HttpMethod.Post, $"{endpoint}/openai/deployments/{deployment}/audio/speech?api-version={SpeechApiVersion}")
            {
                Content = JsonContent.Create(new { model = deployment, input = text, voice = "ash", instructions = ComicDelivery, response_format = "mp3" }),
            };
            request.Headers.Add("api-key", key);
        }
        else
        {
            // Azure Speech on the same AI Services resource. The text is model output going into
            // XML, so it is escaped.
            var ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='en-US'>"
                + "<voice name='en-US-DavisNeural'><mstts:express-as style='excited'><prosody rate='+8%'>"
                + SecurityElement.Escape(text)
                + "</prosody></mstts:express-as></voice></speak>";
            request = new(HttpMethod.Post, $"{endpoint}/tts/cognitiveservices/v1")
            {
                Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"),
            };
            request.Headers.Add("Ocp-Apim-Subscription-Key", key);
            request.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-96kbitrate-mono-mp3");
            request.Headers.Add("User-Agent", "poredomedia");
        }

        return SendAsync(request, voice, TimeSpan.FromSeconds(60), ct);
    }

    /// <summary>The jokes as one sung track from Google Lyria; the audio and its file extension.</summary>
    private async Task<(byte[] Audio, string Extension)> SingAsync(IEnumerable<string> jokes, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LyriaUrl)
        {
            Content = JsonContent.Create(new
            {
                model = AiFoundryClient.Setting(_config, "Google:LyriaModel") ?? "lyria-3-clip-preview",
                input = $"{RapStyle}\n\nPerform these lyrics exactly as written:\n\n[Verse]\n{string.Join('\n', jokes)}",
                response_format = new { type = "audio" },
            }),
        };
        request.Headers.Add("x-goog-api-key", AiFoundryClient.Setting(_config, "Google:ApiKey"));

        // A song takes Lyria 30–90 seconds.
        var body = await SendAsync(request, SessionRoast.Rap, TimeSpan.FromSeconds(150), ct);
        using var doc = JsonDocument.Parse(body);
        return FindAudio(doc.RootElement)
            ?? throw new InvalidOperationException("Lyria returned no audio (its safety filter declines some lyrics).");
    }

    /// <summary>
    /// The first base64 audio payload anywhere in a Lyria answer. The preview API has moved the
    /// field around (<c>data</c>, <c>audio</c>, <c>bytesBase64Encoded</c>), so the search is by shape.
    /// </summary>
    internal static (byte[] Audio, string Extension)? FindAudio(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
            return node.EnumerateArray().Select(FindAudio).FirstOrDefault(a => a is not null);
        if (node.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in (string[])["data", "audio", "b64_audio", "bytesBase64Encoded"])
        {
            if (node.TryGetProperty(name, out var data) && data.ValueKind == JsonValueKind.String
                && data.TryGetBytesFromBase64(out var audio) && audio.Length > 0)
            {
                var mime = node.TryGetProperty("mime_type", out var m) || node.TryGetProperty("mimeType", out m) ? m.GetString() : null;
                return (audio, mime?.Contains("wav", StringComparison.OrdinalIgnoreCase) == true ? "wav" : "mp3");
            }
        }

        return node.EnumerateObject().Select(p => FindAudio(p.Value)).FirstOrDefault(a => a is not null);
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage request, string voice, TimeSpan timeout, CancellationToken ct)
    {
        using (request)
        using (var timed = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timed.CancelAfter(timeout);
            using var response = await _http.SendAsync(request, timed.Token);
            var body = await response.Content.ReadAsByteArrayAsync(timed.Token);
            if (response.IsSuccessStatusCode)
                return body;

            var error = Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 300)));
            _logger.LogWarning("Roast voice {Voice} answered {Status}: {Error}", voice, (int)response.StatusCode, error);
            throw new InvalidOperationException($"The {voice} voice answered {(int)response.StatusCode}.");
        }
    }

    private async Task UploadAsync(string path, byte[] audio, string extension, CancellationToken ct)
    {
        using var stream = new MemoryStream(audio);
        await _blobs.UploadAsync(path, stream, extension == "wav" ? "audio/wav" : "audio/mpeg", ct);
    }
}
