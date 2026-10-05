namespace PoRedoMedia.Shared.Models;

public class SoundAssetDto
{
    public Guid SoundId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public int DurationMs { get; init; }
    public string[] ActionVectorTags { get; init; } = [];
    public string Attribution { get; init; } = string.Empty;

    /// <summary>Whether the calling user has starred this sound.</summary>
    public bool IsFavorite { get; init; }
}
