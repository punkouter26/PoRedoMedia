using System.Security.Cryptography;
using Azure;
using Azure.Data.Tables;

namespace PoRedoMedia.Api.Features.Sharing;

/// <summary>One share link as stored: what it points at, and how often it was opened.</summary>
public sealed record ShareLink(string Token, UserId Owner, MediaId Media, DateTimeOffset SharedAt, int Views);

/// <summary>
/// Share links: one row per token, pointing at one gallery item. Anyone holding the link can view
/// that item until the owner stops sharing it or deletes it.
/// </summary>
public sealed class ShareLinkStore(StorageClients storage) : IShareLinks
{
    private const string Partition = "share";
    private readonly Lazy<TableClient> _table = new(() => storage.Table(StorageNames.Tables.ShareLinks));

    /// <summary>12 characters from 62: about 71 bits, so a link cannot be guessed.</summary>
    internal static string NewToken() =>
        RandomNumberGenerator.GetString("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", 12);

    public Task AddAsync(string token, UserId owner, MediaId media, CancellationToken ct) =>
        _table.Value.AddEntityAsync(new TableEntity(Partition, token)
        {
            ["Owner"] = owner.Value,
            ["MediaId"] = media.ToString(),
            ["SharedAt"] = DateTimeOffset.UtcNow,
        }, ct);

    /// <summary>The link, or null when the token is malformed, unknown or revoked.</summary>
    public async Task<ShareLink?> GetAsync(string token, CancellationToken ct)
    {
        if (token is not { Length: 12 } || !token.All(char.IsAsciiLetterOrDigit))
            return null;

        var row = await _table.Value.GetEntityIfExistsAsync<TableEntity>(Partition, token, cancellationToken: ct);
        return row.HasValue ? Map(row.Value!) : null;
    }

    public async Task<(UserId Owner, MediaId Media)?> ResolveAsync(string token, CancellationToken ct = default) =>
        await GetAsync(token, ct) is { } link ? (link.Owner, link.Media) : null;

    // ponytail: read, add one, write. Two views in the same instant count as one; an ETag retry
    // loop is the upgrade if the number ever needs to be exact.
    public async Task CountViewAsync(string token, CancellationToken ct)
    {
        const string counter = "Views";
        try
        {
            var row = await _table.Value.GetEntityIfExistsAsync<TableEntity>(Partition, token, [counter], ct);
            if (row.HasValue)
            {
                await _table.Value.UpdateEntityAsync(
                    new TableEntity(Partition, token) { [counter] = (row.Value!.GetInt32(counter) ?? 0) + 1 }, ETag.All, TableUpdateMode.Merge, ct);
            }
        }
        catch (RequestFailedException)
        {
            // A counter is not worth failing the page over.
        }
    }

    public async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        try
        {
            await _table.Value.DeleteEntityAsync(Partition, token, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone.
        }
    }

    private static ShareLink Map(TableEntity row) => new(
        row.RowKey, new UserId(row.GetString("Owner")), MediaId.Parse(row.GetString("MediaId"), null),
        row.GetDateTimeOffset("SharedAt") ?? row.Timestamp ?? default, row.GetInt32("Views") ?? 0);
}
