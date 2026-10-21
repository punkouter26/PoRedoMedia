using Microsoft.Extensions.AI;
using SkiaSharp;

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
        if (matchAspectOf is not null && ImageBytes.Identify(matchAspectOf) is { } size)
            height = Math.Max(64, (int)(768.0 * size.Height / size.Width));

        using var image = new SKBitmap(768, height);
        image.Erase(new SKColor(96, 60, 140));
        return Task.FromResult(Png(image));
    }

    public Task<GeneratedImage> EditAsync(string prompt, byte[] image, int seed = 0, CancellationToken ct = default)
    {
        using var loaded = ImageBytes.Decode(image);
        using var edited = new SKBitmap(loaded.Width, loaded.Height);
        using (var canvas = new SKCanvas(edited))
        using (var paint = new SKPaint { ColorFilter = SKColorFilter.CreateCompose(HueTurn(36 * (seed % 10)), Greyed(0.6f)) })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(loaded, 0, 0, SKSamplingOptions.Default, paint);
        }
        return Task.FromResult(Png(edited));
    }

    // The CSS grayscale() and hue-rotate() filter matrices, with Rec. 709 luma weights.
    private const float R = 0.2126f, G = 0.7152f, B = 0.0722f;

    private static SKColorFilter Greyed(float amount)
    {
        var keep = 1 - amount;
        return SKColorFilter.CreateColorMatrix(
        [
            R + (1 - R) * keep, G * amount, B * amount, 0, 0,
            R * amount, G + (1 - G) * keep, B * amount, 0, 0,
            R * amount, G * amount, B + (1 - B) * keep, 0, 0,
            0, 0, 0, 1, 0,
        ]);
    }

    private static SKColorFilter HueTurn(float degrees)
    {
        var cos = MathF.Cos(degrees * MathF.PI / 180);
        var sin = MathF.Sin(degrees * MathF.PI / 180);
        return SKColorFilter.CreateColorMatrix(
        [
            R + cos * (1 - R) - sin * R, G - cos * G - sin * G, B - cos * B + sin * (1 - B), 0, 0,
            R - cos * R + sin * 0.143f, G + cos * (1 - G) + sin * 0.140f, B - cos * B - sin * 0.283f, 0, 0,
            R - cos * R - sin * (1 - R), G - cos * G + sin * G, B + cos * (1 - B) + sin * B, 0, 0,
            0, 0, 0, 1, 0,
        ]);
    }

    private static GeneratedImage Png(SKBitmap image) => new(ImageBytes.ToPng(image), "image/png");
}

/// <summary>Stands in for music generation with a tiny silent MP3.</summary>
public sealed class MockMusicService : IMusicGenerationService
{
    private static readonly byte[] SilentMp3 = Convert.FromBase64String(
        "SUQzBAAAAAAAI1RTU0UAAAAPAAADTGF2ZjU4Ljc2LjEwMAAAAAAAAAAAAAAA//tQxAADwAAB"
        + "pAAAACAAADSAAAAETEFNRTMuMTAwVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV"
        + "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV");

    public bool IsConfigured => true;

    public Task<MusicGenerationResult> GenerateAsync(string lyrics, string stylePrompt, CancellationToken ct = default) =>
        Task.FromResult(new MusicGenerationResult(SilentMp3, "audio/mpeg", 1));
}
