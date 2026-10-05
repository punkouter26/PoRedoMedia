using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Api.Features.MemeCaption;
using PoRedoMedia.Shared;

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
    public void Gemini_text_is_taken_from_the_first_candidate_and_is_empty_when_there_is_none()
    {
        using var answered = JsonDocument.Parse("""{"candidates":[{"content":{"parts":[{"text":"hello"}]}}]}""");
        using var blocked = JsonDocument.Parse("""{"promptFeedback":{"blockReason":"SAFETY"}}""");

        Assert.Equal("hello", GeminiVisionService.Text(answered.RootElement));
        Assert.Equal("", GeminiVisionService.Text(blocked.RootElement));
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
    public async Task The_caption_writer_sends_the_description_and_refuses_an_empty_caption()
    {
        var chat = new StubChat("""{"top":"When the box arrives","bottom":"And the cat approves"}""");

        var caption = await new ChatCaptionWriter(chat).WriteAsync("A cat in a box", ["cat", "box"]);

        Assert.Equal(new MemeCaptionText("When the box arrives", "And the cat approves"), caption);
        Assert.Contains(chat.Seen, m => m.Role == ChatRole.User && m.Text.Contains("A cat in a box") && m.Text.Contains("cat, box"));
        await Assert.ThrowsAsync<RunStepException>(() => new ChatCaptionWriter(new StubChat("""{"top":"","bottom":""}""")).WriteAsync("x", []));
    }
}
