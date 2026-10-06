namespace PoRedoMedia.Api.Common;

public class SoundAsset
{
    public SoundId SoundId { get; init; } = SoundId.New();
    public required string DisplayName { get; set; }
    public int DurationMs { get; set; }
    public string[] ActionVectorTags { get; set; } = [];
    public required string BlobUrl { get; set; }

    /// <summary>
    /// <see cref="BlobUrl"/> as the <c>container/blob</c> path <see cref="BlobStorageService"/> takes.
    /// Seeded rows hold a full URL (<c>https://{account}.blob.core.windows.net/sounds/x.mp3</c>, or
    /// Azurite's <c>http://127.0.0.1:10000/devstoreaccount1/sounds/x.mp3</c>); uploads hold the path.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string BlobPath
    {
        get
        {
            if (!Uri.TryCreate(BlobUrl, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http", StringComparison.Ordinal))
                return BlobUrl;

            var path = uri.AbsolutePath.TrimStart('/');
            // Azurite puts the account name first in the path; Azure has it in the host.
            return path.StartsWith("devstoreaccount", StringComparison.OrdinalIgnoreCase) && path.IndexOf('/') is var slash and >= 0
                ? path[(slash + 1)..]
                : path;
        }
    }

    /// <summary>Curated wojak-storytelling staples the director should favor over generic matches.</summary>
    /// <summary>
    /// Who uploaded it, as <see cref="UserId.Key"/>. Null for the seeded library, which everyone
    /// shares. An uploaded sound is its uploader's alone: its name and tags are quoted in the
    /// director's prompt, so sharing it would let one user steer another user's video.
    /// </summary>
    public string? Owner { get; init; }

    public bool Priority { get; set; }

    /// <summary>Human-readable hint for the AI director describing when this sound fits.</summary>
    public string UseCase { get; set; } = string.Empty;

    /// <summary>Licence credit for third-party sounds that need one; empty otherwise.</summary>
    public string Attribution { get; set; } = string.Empty;
}

public static class SoundLibrary
{
    /// <summary>The shared library plus this user's own uploads.</summary>
    public static IReadOnlyList<SoundAsset> VisibleTo(this IReadOnlyList<SoundAsset> sounds, UserId user) =>
        [.. sounds.Where(s => s.Owner is null || s.Owner == user.Key)];
}
