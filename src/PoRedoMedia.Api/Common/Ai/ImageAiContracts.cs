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
