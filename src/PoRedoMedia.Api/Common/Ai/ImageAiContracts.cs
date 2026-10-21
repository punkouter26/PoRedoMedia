namespace PoRedoMedia.Api.Common.Ai;

// Contracts for the image-side AI providers. Each has real implementations and a mock.

/// <param name="FallbackReason">Set when the provider degraded, such as returning tags in place of a caption. Shown to the user.</param>
public sealed record VisionResult(string Description, IReadOnlyList<string> Tags, double Confidence, string? FallbackReason = null);

public interface IVisionService
{
    Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default);
}

public sealed record GeneratedImage(byte[] Data, string ContentType)
{
    public string Extension => ContentType == "image/jpeg" ? ".jpg" : ".png";
}

public interface IImageGenerationService
{
    /// <summary>Draws a new image from words alone. <paramref name="matchAspectOf"/> only lends its shape.</summary>
    Task<GeneratedImage> GenerateAsync(string prompt, byte[]? matchAspectOf = null, CancellationToken ct = default);

    /// <summary>Redraws <paramref name="image"/> as the prompt says. A non-zero seed asks for a different take.</summary>
    Task<GeneratedImage> EditAsync(string prompt, byte[] image, int seed = 0, CancellationToken ct = default);
}

/// <summary>
/// One chat call: a system prompt, a user prompt, optionally a picture, optionally a JSON schema
/// the answer must follow. <see cref="IsConfigured"/> is false when no chat model is available;
/// callers then use their own fallback and say so.
/// </summary>
public interface IChatCompletionService
{
    bool IsConfigured { get; }

    Task<ChatCompletionResult> CompleteAsync(
        string systemPrompt, string userPrompt, byte[]? image = null, string? jsonSchema = null, CancellationToken ct = default);
}

public sealed record ChatCompletionResult(string Content, int TokensUsed, long ElapsedMs);

public interface IMusicGenerationService
{
    bool IsConfigured { get; }

    Task<MusicGenerationResult> GenerateAsync(string lyrics, string stylePrompt, CancellationToken ct = default);
}

/// <param name="Refused">The provider declined the lyrics. Not an error: the caller softens them and tries again.</param>
public sealed record MusicGenerationResult(byte[] Audio, string ContentType, long ElapsedMs, bool Refused = false, string? RefusalReason = null)
{
    public static MusicGenerationResult FromRefusal(long elapsedMs, string? reason) =>
        new([], string.Empty, elapsedMs, Refused: true, RefusalReason: reason);
}

public interface IVideoGenerationService
{
    /// <summary>
    /// Animates a picture as the prompt describes and returns the finished MP4. Takes minutes.
    /// Throws <see cref="RunStepException"/> when the provider rejects the request or runs out of time.
    /// </summary>
    Task<byte[]> GenerateAsync(byte[] image, string prompt, CancellationToken ct = default);
}
