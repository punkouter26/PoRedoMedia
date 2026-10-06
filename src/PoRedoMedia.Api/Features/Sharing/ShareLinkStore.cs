using System.Security.Cryptography;
using Azure;
using Azure.Data.Tables;

namespace PoRedoMedia.Api.Features.Sharing;

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
        _table.Value.AddEntityAsync(new TableEntity(Partition, token) { ["Owner"] = owner.Value, ["MediaId"] = media.ToString() }, ct);

    /// <summary>Whose item a token points at, or null when the token is malformed, unknown or revoked.</summary>
    public async Task<(UserId Owner, MediaId Media)?> ResolveAsync(string token, CancellationToken ct)
    {
        if (token is not { Length: 12 } || !token.All(char.IsAsciiLetterOrDigit))
            return null;

        var row = await _table.Value.GetEntityIfExistsAsync<TableEntity>(Partition, token, cancellationToken: ct);
        return row.HasValue ? (new UserId(row.Value!.GetString("Owner")), MediaId.Parse(row.Value.GetString("MediaId"), null)) : null;
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
}
