using Azure;
using Azure.Data.Tables;
using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Api.Features.Media;

/// <summary>Media rows: partition = owner, row = media id, so every read is scoped to one user.</summary>
public sealed class MediaTableRepository(StorageClients storage) : IMediaRepository
{
    private readonly Lazy<TableClient> _table = new(() => storage.Table(StorageNames.Tables.Media));

    public Task SaveAsync(MediaItem item, CancellationToken ct = default) =>
        _table.Value.UpsertEntityAsync(new TableEntity(item.Owner.Key, item.Id.ToString())
        {
            ["Owner"] = item.Owner.Value,
            ["Kind"] = item.Kind.ToString(),
            ["Status"] = item.Status.ToString(),
            ["Origin"] = item.Origin,
            ["ParentId"] = item.ParentId?.ToString(),
            ["Title"] = item.Title,
            ["ContentType"] = item.ContentType,
            ["Extension"] = item.Extension,
            ["SizeBytes"] = item.SizeBytes,
            ["DurationSeconds"] = item.DurationSeconds,
            ["Pinned"] = item.Pinned,
            ["ShareToken"] = item.ShareToken,
            ["CreatedAt"] = item.CreatedAt,
            ["Text"] = item.Text,
            ["Detail"] = item.Detail,
        }, TableUpdateMode.Replace, ct);

    public async Task<MediaItem?> GetAsync(UserId owner, MediaId id, CancellationToken ct = default)
    {
        var found = await _table.Value.GetEntityIfExistsAsync<TableEntity>(owner.Key, id.ToString(), cancellationToken: ct);
        return found.HasValue ? Map(found.Value!) : null;
    }

    public async Task<IReadOnlyList<MediaItem>> ListAsync(UserId owner, CancellationToken ct = default)
    {
        var items = new List<MediaItem>();
        await foreach (var row in _table.Value.QueryAsync<TableEntity>(e => e.PartitionKey == owner.Key, cancellationToken: ct))
            items.Add(Map(row));
        return [.. items.OrderByDescending(m => m.CreatedAt)];
    }

    public async Task<IReadOnlyList<MediaItem>> ListAllAsync(CancellationToken ct = default)
    {
        var items = new List<MediaItem>();
        await foreach (var row in _table.Value.QueryAsync<TableEntity>(cancellationToken: ct))
            items.Add(Map(row));
        return items;
    }

    public async Task DeleteAsync(UserId owner, MediaId id, CancellationToken ct = default)
    {
        try
        {
            await _table.Value.DeleteEntityAsync(owner.Key, id.ToString(), cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone.
        }
    }

    private static MediaItem Map(TableEntity row) => new()
    {
        Owner = new UserId(row.GetString("Owner")),
        Id = MediaId.Parse(row.RowKey, null),
        Kind = Enum.Parse<MediaKind>(row.GetString("Kind")),
        Status = Enum.Parse<MediaStatus>(row.GetString("Status")),
        Origin = row.GetString("Origin"),
        ParentId = row.GetString("ParentId") is { } parent ? MediaId.Parse(parent, null) : null,
        Title = row.GetString("Title"),
        ContentType = row.GetString("ContentType"),
        Extension = row.GetString("Extension"),
        SizeBytes = row.GetInt64("SizeBytes") ?? 0,
        DurationSeconds = row.GetDouble("DurationSeconds"),
        Pinned = row.GetBoolean("Pinned") ?? false,
        ShareToken = row.GetString("ShareToken"),
        CreatedAt = row.GetDateTimeOffset("CreatedAt") ?? row.Timestamp ?? default,
        Text = row.GetString("Text"),
        Detail = row.GetString("Detail"),
    };
}
