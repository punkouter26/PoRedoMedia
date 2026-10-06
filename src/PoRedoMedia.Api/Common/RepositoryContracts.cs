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

    /// <summary>Every user's items. For the retention sweep only.</summary>
    Task<IReadOnlyList<MediaItem>> ListAllAsync(CancellationToken ct = default);
}

public interface IRunRepository
{
    /// <summary>Creates or replaces the run.</summary>
    Task SaveAsync(Run run, CancellationToken ct = default);

    /// <summary>Null when the run does not exist or belongs to someone else.</summary>
    Task<Run?> GetAsync(UserId owner, RunId id, CancellationToken ct = default);

    /// <summary>The owner's runs, newest first.</summary>
    Task<IReadOnlyList<Run>> ListAsync(UserId owner, CancellationToken ct = default);

    /// <summary>Every user's runs that have not finished. For the sweep that recovers interrupted ones.</summary>
    Task<IReadOnlyList<Run>> ListUnfinishedAsync(CancellationToken ct = default);
}

public interface ISoundAssetRepository
{
    Task<IReadOnlyList<SoundAsset>> LoadAllAsync(CancellationToken cancellationToken = default);

    Task AddSoundAsync(SoundAsset asset, CancellationToken cancellationToken = default);

    Task DeleteSoundAsync(SoundId soundId, CancellationToken cancellationToken = default);
}

public interface ISoundFavoritesRepository
{
    Task<IReadOnlySet<SoundId>> GetAsync(UserId userId, CancellationToken cancellationToken = default);

    Task SetAsync(UserId userId, SoundId soundId, bool favorite, CancellationToken cancellationToken = default);
}
