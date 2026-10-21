using System.Text.Json;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// What a model read off one item's picture, kept under the item (<c>media/{id}/ai/</c>), so a
/// second run on the same picture does not pay for the same look again, and the answer survives a
/// restart. It goes with the item: the item's prefix delete removes it.
/// </summary>
/// <remarks>
/// Only answers worth reusing are kept: a caller passes null for a degraded one (a fallback, an
/// empty read), so the next run asks again. A file that cannot be read is treated as absent.
/// </remarks>
public sealed class AiReadStore(BlobStorageService blobs, ILogger<AiReadStore> logger)
{
    public async Task<T?> GetAsync<T>(MediaId item, string name, CancellationToken ct) where T : class
    {
        var path = MediaAnalysisPaths.AiRead(item.Value, name);
        try
        {
            if (!await blobs.ExistsAsync(path, ct))
                return null;
            return JsonSerializer.Deserialize<T>(await blobs.ReadAllBytesAsync(path, ct), JsonSerializerOptions.Web);
        }
        catch (Exception ex) when (ex is JsonException or Azure.RequestFailedException)
        {
            logger.LogWarning(ex, "Stored AI read {Path} could not be read; asking the model again", path);
            return null;
        }
    }

    public async Task SaveAsync<T>(MediaId item, string name, T read, CancellationToken ct) where T : class
    {
        try
        {
            await blobs.UploadAsync(
                MediaAnalysisPaths.AiRead(item.Value, name), JsonSerializer.SerializeToUtf8Bytes(read, JsonSerializerOptions.Web), "application/json", ct);
        }
        catch (Azure.RequestFailedException ex)
        {
            // Keeping it is a saving, not part of the result.
            logger.LogWarning(ex, "AI read {Name} for {MediaId} could not be stored", name, item);
        }
    }

    /// <summary>
    /// The stored read, or a fresh one from <paramref name="read"/>, stored when
    /// <paramref name="worthKeeping"/> says so.
    /// </summary>
    public async Task<T> GetOrReadAsync<T>(MediaId item, string name, Func<Task<T>> read, Func<T, bool> worthKeeping, CancellationToken ct)
        where T : class
    {
        if (await GetAsync<T>(item, name, ct) is { } stored)
            return stored;

        var fresh = await read();
        if (worthKeeping(fresh))
            await SaveAsync(item, name, fresh, ct);
        return fresh;
    }
}
