namespace PoRedoMedia.Mobile.Models;

/// <summary>
/// Metadata associated with an AI-generated media asset for EXIF tagging and media cataloging.
/// </summary>
/// <param name="Title">A short title or headline for the asset.</param>
/// <param name="PromptOrDescription">The prompt, roast lyrics, or scene description that generated the asset.</param>
/// <param name="ModelOrStyle">The AI model or art style used (e.g. "Gemini Flash / Cyberpunk", "Qwen2.5 0.5B").</param>
/// <param name="CreatedAt">The timestamp of creation.</param>
/// <param name="Author">The creator tag, defaults to "PoRedoMedia AI Studio".</param>
public sealed record MediaMetadata(
    string? Title = null,
    string? PromptOrDescription = null,
    string? ModelOrStyle = null,
    DateTimeOffset? CreatedAt = null,
    string Author = "PoRedoMedia AI Studio");

