namespace PoRedoMedia.Api.Common;

// Contracts a slice uses to reach storage another slice owns. A slice depends on these, never on
// a sibling slice's types.

public interface IMediaRepository
{
    /// <summary>Creates or replaces the item.</summary>
    Task SaveAsync(MediaItem item, CancellationToken ct = default);

    /// <summary>Null when the item does not exist or belongs to someone else.</summary>
    Task<MediaItem?> GetAsync(UserId owner, MediaId id, CancellationToken ct = default);

    /// <summary>The owner's items, newest first.</summary>
    Task<IReadOnlyList<MediaItem>> ListAsync(UserId owner, CancellationToken ct = default);

    Task DeleteAsync(UserId owner, MediaId id, CancellationToken ct = default);
}
