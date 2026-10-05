namespace PoRedoMedia.Api.Common;

/// <summary>
/// The sound library's tag vocabulary, most used first. Vision labels frames with it, and the
/// tagger labels new sounds with it, so scenes and sounds end up speaking the same words.
/// </summary>
public static class SoundVocabulary
{
    /// <summary>Provenance markers, not descriptions of a sound.</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { "custom", "user-upload" };

    public static IReadOnlyList<string> Tags(IEnumerable<SoundAsset> library)
        => [.. library
            .SelectMany(s => s.ActionVectorTags)
            .Where(t => !string.IsNullOrWhiteSpace(t) && !Excluded.Contains(t))
            .GroupBy(t => t.Trim().ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)];
}
