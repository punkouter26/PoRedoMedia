using Azure.Data.Tables;

namespace PoRedoMedia.Api.Features.Sounds;

/// <summary>
/// One row per (user, sound): PartitionKey = user id, RowKey = sound id. Reading a user's
/// favourites is then a single-partition query, which is the only read this table serves.
/// </summary>
public sealed class SoundFavoritesTableRepository : ISoundFavoritesRepository
{
    private readonly StorageClients _factory;

    public SoundFavoritesTableRepository(StorageClients factory) => _factory = factory;

    public async Task<IReadOnlySet<SoundId>> GetAsync(UserId userId, CancellationToken cancellationToken = default)
    {
        var table = _factory.Table(StorageNames.Tables.SoundFavorites);
        var result = new HashSet<SoundId>();
        await foreach (var entity in table.QueryAsync<TableEntity>(
                           filter: TableClient.CreateQueryFilter($"PartitionKey eq {userId.ToString()}"),
                           select: ["RowKey"],
                           cancellationToken: cancellationToken))
        {
            if (Guid.TryParse(entity.RowKey, out var id))
                result.Add(new SoundId(id));
        }

        return result;
    }

    public async Task SetAsync(UserId userId, SoundId soundId, bool favorite, CancellationToken cancellationToken = default)
    {
        var table = _factory.Table(StorageNames.Tables.SoundFavorites);
        if (favorite)
        {
            await table.UpsertEntityAsync(
                new TableEntity(userId.ToString(), soundId.ToString()) { ["StarredAt"] = DateTimeOffset.UtcNow },
                TableUpdateMode.Replace,
                cancellationToken);
        }
        else
        {
            await table.DeleteEntityAsync(userId.ToString(), soundId.ToString(), cancellationToken: cancellationToken);
        }
    }
}
