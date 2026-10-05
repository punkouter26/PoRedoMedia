using Azure.Storage.Blobs.Models;

namespace PoRedoMedia.Api.Common;

/// <summary>All blob reads and writes. Paths are written <c>container/name</c>.</summary>
public sealed class BlobStorageService(StorageClients storage)
{
    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        await storage.Blob(path).ExistsAsync(ct);

    public Task UploadAsync(string path, Stream content, string contentType, CancellationToken ct = default) =>
        storage.Blob(path).UploadAsync(
            content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);

    public Task UploadAsync(string path, byte[] content, string contentType, CancellationToken ct = default) =>
        UploadAsync(path, new MemoryStream(content), contentType, ct);

    /// <summary>
    /// A seekable stream of known length. A plain download stream has neither, and a browser
    /// served video without a length stops buffering after the first chunk.
    /// </summary>
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
        storage.Blob(path).OpenReadAsync(cancellationToken: ct);

    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default) =>
        (await storage.Blob(path).DownloadContentAsync(ct)).Value.Content.ToArray();

    public Task DownloadToFileAsync(string path, string localFilePath, CancellationToken ct = default) =>
        storage.Blob(path).DownloadToAsync(localFilePath, ct);

    /// <summary>Removes every blob under a prefix, which is how a media item is deleted whole.</summary>
    public async Task DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        var slash = prefix.IndexOf('/');
        var container = storage.Container(prefix[..slash]);
        await foreach (var blob in container.GetBlobsAsync(prefix: prefix[(slash + 1)..], cancellationToken: ct))
            await container.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: ct);
    }
}
