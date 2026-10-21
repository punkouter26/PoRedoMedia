using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Api.Features.Cutout;
using PoRedoMedia.Api.Features.MemeCaption;
using PoRedoMedia.Api.Features.Memeify;
using PoRedoMedia.Shared;
using SkiaSharp;

namespace PoRedoMedia.UnitTests;

public sealed class ImageAiTests
{
    private static readonly byte[] Image = [1, 2, 3];

    private sealed class StubChat(string reply) : IChatClient
    {
        public List<ChatMessage> Seen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Answers each call with the next reply, then repeats the last.</summary>
    private sealed class ScriptedChat(params string[] replies) : IChatClient
    {
        private int _next;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies[Math.Min(_next++, replies.Length - 1)])));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class CountingVision(string description) : IVisionService
    {
        public int Calls { get; private set; }

        public Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new VisionResult(description, [], 1));
        }
    }

    [Theory]
    [InlineData(null, "gemini")]
    [InlineData("", "gemini")]
    [InlineData(AiProviderIds.AzureOpenAiVision, "openai")]
    [InlineData(AiProviderIds.AzureComputerVision, "cv")]
    public async Task The_router_returns_the_picked_provider_and_prefers_Gemini_by_default(string? modelId, string expected)
    {
        var router = new VisionServiceRouter(new Dictionary<string, IVisionService>
        {
            [AiProviderIds.AzureComputerVision] = new CountingVision("cv"),
            [AiProviderIds.AzureOpenAiVision] = new CountingVision("openai"),
            [AiProviderIds.GeminiVision] = new CountingVision("gemini"),
        });

        Assert.Equal(expected, (await router.Resolve(modelId).AnalyzeAsync(Image)).Description);
    }

    [Fact]
    public void A_model_the_server_does_not_have_is_refused_rather_than_replaced_by_another()
    {
        var router = new VisionServiceRouter(new Dictionary<string, IVisionService> { [AiProviderIds.GeminiVision] = new CountingVision("gemini") });

        Assert.Throws<RunStepException>(() => router.Resolve("browser:florence2-base"));
        Assert.Throws<RunStepException>(() => router.Resolve(AiProviderIds.OllamaVision));
        Assert.Throws<RunStepException>(() => new VisionServiceRouter(new Dictionary<string, IVisionService>()).Resolve(null));
    }

    [Theory]
    [InlineData("""{"description":" A dog on a skateboard. ","tags":["Dog","skateboard","dog",""]}""", "A dog on a skateboard.", "dog,skateboard")]
    [InlineData("A dog on a skateboard.", "A dog on a skateboard.", "")]
    public void A_vision_answer_is_read_as_json_or_kept_as_prose(string answer, string description, string tags)
    {
        var result = VisionJson.Parse(answer);

        Assert.Equal((description, tags), (result.Description, string.Join(',', result.Tags)));
    }

    [Fact]
    public void Gemini_answers_are_read_text_usage_and_whole_clip_moments_alike()
    {
        using var answered = JsonDocument.Parse("""{"candidates":[{"content":{"parts":[{"text":"hello"}]}}]}""");
        using var blocked = JsonDocument.Parse("""{"promptFeedback":{"blockReason":"SAFETY"}}""");

        Assert.Equal("hello", GeminiVisionService.Text(answered.RootElement));
        Assert.Equal("", GeminiVisionService.Text(blocked.RootElement));

        // What a call cost is read off the answer and the address it went to.
        Assert.Equal("gemini-flash-lite-latest", AiMeterHandler.ModelOf("/v1beta/models/gemini-flash-lite-latest:generateContent"));
        Assert.Equal((120, 20, 35), AiMeterHandler.GeminiUsage(Encoding.UTF8.GetBytes(
            """{"usageMetadata":{"promptTokenCount":120,"cachedContentTokenCount":20,"candidatesTokenCount":30,"thoughtsTokenCount":5}}""")));
        Assert.Equal((0, 0, 0), AiMeterHandler.GeminiUsage(Encoding.UTF8.GetBytes("not json")));

        // Whole-clip moments stay inside the clip, in time order, with tags only from the vocabulary.
        var labels = GeminiClipVisionService.Parse(
            """
            {"moments":[
              {"seconds":7.5,"label":"crowd gasps","energy":1.4,"tags":["shock","made-up"],"subjectX":0.5,"subjectY":null},
              {"seconds":1.25,"label":"man slips on ice","energy":0.9,"tags":["fail"]},
              {"seconds":12,"label":"after the end","energy":0.5,"tags":[]},
              {"seconds":3,"label":"  ","energy":0.5,"tags":[]}
            ]}
            """,
            new HashSet<string>(["fail", "shock"], StringComparer.OrdinalIgnoreCase), durationSeconds: 10);
        Assert.Equal(["man slips on ice", "crowd gasps"], labels.Select(l => l.Label));
        Assert.Equal([1.25, 7.5], labels.Select(l => l.TimestampSeconds));
        Assert.Equal((1.0, "shock", 0.5, (double?)null), (labels[1].Energy, Assert.Single(labels[1].Tags), labels[1].SubjectX, labels[1].SubjectY));
    }

    [Fact]
    public async Task A_chat_model_is_shown_the_picture_and_an_empty_answer_is_a_readable_failure()
    {
        var chat = new StubChat("""{"description":"A cat in a box","tags":["cat"]}""");

        var result = await new ChatVisionService(chat).AnalyzeAsync(Image);

        Assert.Equal("A cat in a box", result.Description);
        Assert.Contains(chat.Seen.SelectMany(m => m.Contents).OfType<DataContent>(), c => c.Data.ToArray().SequenceEqual(Image));
        await Assert.ThrowsAsync<RunStepException>(() => new ChatVisionService(new StubChat("{}")).AnalyzeAsync(Image));
    }

    [Fact]
    public async Task The_same_picture_is_analysed_once_per_provider()
    {
        var cache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();
        var inner = new CountingVision("seen");
        var gemini = new CachingVisionService(inner, cache, "gemini");
        var other = new CachingVisionService(inner, cache, "cv");

        await gemini.AnalyzeAsync(Image);
        await gemini.AnalyzeAsync(Image);
        await gemini.AnalyzeAsync([9, 9]);
        await other.AnalyzeAsync(Image);

        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task The_caption_writer_sends_the_description_refuses_an_empty_caption_and_keeps_the_judges_pick()
    {
        var chat = new StubChat("""{"top":"When the box arrives","bottom":"And the cat approves"}""");

        var caption = await new ChatCaptionWriter(chat).WriteAsync("A cat in a box", ["cat", "box"]);

        Assert.Equal(new MemeCaptionText("When the box arrives", "And the cat approves"), caption);
        Assert.Contains(chat.Seen, m => m.Role == ChatRole.User && m.Text.Contains("A cat in a box") && m.Text.Contains("cat, box"));
        await Assert.ThrowsAsync<RunStepException>(() => new ChatCaptionWriter(new StubChat("""{"top":"","bottom":""}""")).WriteAsync("x", []));

        // With a judge, several drafts are written and the best scored one is kept.
        var drafts = new ScriptedChat(
            """{"captions":[{"top":"a","bottom":"1"},{"top":"b","bottom":"2"},{"top":"","bottom":""}]}""",
            """{"scores":[{"draft":0,"score":3},{"draft":1,"score":9}]}""");
        Assert.Equal(new MemeCaptionText("b", "2"), await new ChatCaptionWriter(drafts, new AiJudge(drafts, NullLogger<AiJudge>.Instance)).WriteAsync("x", []));

        // A judge that gives no scores keeps the first draft rather than failing the caption.
        var broken = new ScriptedChat("""{"captions":[{"top":"a","bottom":"1"},{"top":"b","bottom":"2"}]}""", "I refuse.");
        Assert.Equal(new MemeCaptionText("a", "1"), await new ChatCaptionWriter(broken, new AiJudge(broken, NullLogger<AiJudge>.Instance)).WriteAsync("x", []));
    }

    [Fact]
    public async Task A_sticker_is_a_512_px_webp_of_the_subject_with_a_white_outline_and_clear_corners()
    {
        using var photo = new SKBitmap(400, 300);
        photo.Erase(new SKColor(20, 40, 160));
        var cut = await new MockBackgroundRemover().RemoveAsync(ImageBytes.ToPng(photo), "image/png", default);

        var webp = Sticker.Make(cut);

        using var codec = SKCodec.Create(new MemoryStream(webp));
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        using var sticker = SKBitmap.Decode(webp);
        Assert.Equal((Sticker.Size, Sticker.Size), (sticker.Width, sticker.Height));
        Assert.Equal(0, sticker.GetPixel(2, 2).Alpha);
        // The subject fills the middle; the outline runs just outside its left edge, at x = 16.
        Assert.True(sticker.GetPixel(256, 256) is { Alpha: 255, Blue: > 120, Red: < 60 });
        Assert.True(sticker.GetPixel(10, 256) is { Alpha: > 200, Red: > 230, Green: > 230, Blue: > 230 });
        Assert.True(Sticker.HasSubject(cut));
        using var empty = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        empty.Erase(SKColors.Transparent);
        Assert.False(Sticker.HasSubject(ImageBytes.ToPng(empty)));
    }
}
