using Microsoft.Extensions.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PoRedoMedia.Api.Common.Ai;

// Mock providers, registered when MockAi.IsEnabled. None makes a network call, so mock mode
// cannot spend anything.

public sealed class MockVisionService : IVisionService, IVisionServiceRouter
{
    public Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default) =>
        Task.FromResult(new VisionResult("A mock description of the picture, written with no AI call.", ["mock", "sample"], 0.99));

    public IVisionService Resolve(string? modelId) => this;
}

/// <summary>Answers every chat request with one fixed sentence.</summary>
public sealed class MockChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "A mock answer, written with no AI call.")));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// Stands in for image generation with something visibly different from the input, so a mock run
/// still shows that a step happened: a plain colour field, or the input washed towards grey.
/// </summary>
public sealed class MockImageGenerationService : IImageGenerationService
{
    public Task<GeneratedImage> GenerateAsync(string prompt, byte[]? matchAspectOf = null, CancellationToken ct = default)
    {
        var height = 768;
        if (matchAspectOf is not null)
        {
            var info = Image.Identify(matchAspectOf);
            height = Math.Max(64, (int)(768.0 * info.Height / info.Width));
        }

        using var image = new Image<Rgba32>(768, height, new Rgba32(96, 60, 140));
        return Task.FromResult(Png(image));
    }

    public Task<GeneratedImage> EditAsync(string prompt, byte[] image, int seed = 0, CancellationToken ct = default)
    {
        using var loaded = Image.Load(image);
        loaded.Mutate(x => x.Grayscale(0.6f).Hue(36 * (seed % 10)));
        return Task.FromResult(Png(loaded));
    }

    private static GeneratedImage Png(Image image)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return new GeneratedImage(stream.ToArray(), "image/png");
    }
}
