namespace PoRedoMedia.Api.Common.Ai;

// Contracts for the image-side AI providers. Each has real implementations and a mock.

/// <param name="FallbackReason">Set when the provider degraded, such as returning tags in place of a caption. Shown to the user.</param>
public sealed record VisionResult(string Description, IReadOnlyList<string> Tags, double Confidence, string? FallbackReason = null);

public interface IVisionService
{
    Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default);
}

/// <summary>Picks the vision provider for a model id chosen in the model picker. Null picks the default.</summary>
public interface IVisionServiceRouter
{
    IVisionService Resolve(string? modelId);
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

/// <summary>Ground-truth facts read off an image: printed text, captioned regions, objects, people.</summary>
public interface ISceneDetailProvider
{
    bool IsConfigured { get; }

    Task<SceneDetails> GetDetailsAsync(byte[] imageData, CancellationToken ct = default);
}

public sealed record SceneDetails(
    IReadOnlyList<string> TextLines, IReadOnlyList<string> RegionCaptions, IReadOnlyList<string> Objects, int PeopleCount, long ElapsedMs)
{
    public static SceneDetails Empty { get; } = new([], [], [], 0, 0);

    public bool HasAny => TextLines.Count > 0 || RegionCaptions.Count > 0 || Objects.Count > 0;

    public string ToPromptBlock()
    {
        var parts = new List<string>();
        if (TextLines.Count > 0)
            parts.Add($"Text visible in the image (read by OCR, treat as exact): {string.Join(" | ", TextLines)}");
        if (RegionCaptions.Count > 0)
            parts.Add($"Region captions: {string.Join("; ", RegionCaptions)}");
        if (Objects.Count > 0)
            parts.Add($"Detected objects: {string.Join(", ", Objects)}");
        if (PeopleCount > 0)
            parts.Add($"People detected: {PeopleCount}");
        return string.Join('\n', parts);
    }
}

/// <summary>A provider that can return description, tags and scene details in one call.</summary>
public interface ICombinedVisionAnalyzer
{
    bool SupportsCombinedAnalysis { get; }

    Task<CombinedVisionResult> AnalyzeAllAsync(byte[] imageData, CancellationToken ct = default);
}

public sealed record CombinedVisionResult(string Description, IReadOnlyList<string> Tags, double ConfidenceScore, SceneDetails Details, long ElapsedMs);

public interface IVideoGenerationService
{
    /// <summary>
    /// Animates a picture as the prompt describes and returns the finished MP4. Takes minutes.
    /// Throws <see cref="RunStepException"/> when the provider rejects the request or runs out of time.
    /// </summary>
    Task<byte[]> GenerateAsync(byte[] image, string prompt, CancellationToken ct = default);
}
