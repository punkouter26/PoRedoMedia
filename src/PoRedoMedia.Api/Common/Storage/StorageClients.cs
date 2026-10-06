using System.Collections.Concurrent;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// Table and blob clients for the app's one storage account. Blob paths throughout the app are
/// written <c>container/name</c>.
/// </summary>
public sealed class StorageClients(IConfiguration configuration)
{
    private readonly string? _connectionString = configuration[ConfigKeys.StorageConnectionString];

    // Unconfigured storage throws on first use. It must never look like a successful save.
    private string ConnectionString => !string.IsNullOrWhiteSpace(_connectionString)
        ? _connectionString
        : throw new InvalidOperationException($"{ConfigKeys.StorageConnectionString} is not configured.");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    // One client per name, created once: "create if not exists" is a network call, and it used
    // to run on every blob and table operation.
    private readonly ConcurrentDictionary<string, Lazy<TableClient>> _tables = new();
    private readonly ConcurrentDictionary<string, Lazy<BlobContainerClient>> _containers = new();

    public TableClient Table(string name) => _tables.GetOrAdd(name, n => new(() =>
    {
        var table = new TableClient(ConnectionString, n);
        table.CreateIfNotExists();
        return table;
    })).Value;

    public BlobContainerClient Container(string name) => _containers.GetOrAdd(name, n => new(() =>
    {
        var container = new BlobContainerClient(ConnectionString, n);
        container.CreateIfNotExists();
        return container;
    })).Value;

    public BlobClient Blob(string path)
    {
        var slash = path.IndexOf('/');
        if (slash < 0)
            throw new ArgumentException($"Blob path must start with a container: {path}", nameof(path));
        return Container(path[..slash]).GetBlobClient(path[(slash + 1)..]);
    }

    /// <summary>A link the browser can PUT one blob to, so uploads never pass through this app.</summary>
    public Task<Uri> CreateUploadLinkAsync(string path, DateTimeOffset expiresAt) =>
        Task.FromResult(Blob(path).GenerateSasUri(BlobSasPermissions.Write | BlobSasPermissions.Create, expiresAt));

    /// <summary>
    /// A short-lived link the browser can read one blob from. With a file name the link also
    /// forces a download, which the <c>download</c> attribute cannot do across origins.
    /// </summary>
    public Uri CreateReadLink(string path, TimeSpan lifetime, string? downloadFileName = null)
    {
        var blob = Blob(path);
        return blob.GenerateSasUri(new BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(lifetime))
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
            ContentDisposition = downloadFileName is null ? null : $"attachment; filename=\"{downloadFileName}\"",
        });
    }

    /// <summary>Allows the browser to upload to and read from blob storage from the app's origins.</summary>
    public async Task AllowBrowserAccessAsync(string allowedOrigins, CancellationToken ct = default)
    {
        var service = new BlobServiceClient(ConnectionString);
        var properties = (await service.GetPropertiesAsync(ct)).Value;
        properties.Cors.Clear();
        properties.Cors.Add(new BlobCorsRule
        {
            AllowedOrigins = allowedOrigins,
            AllowedMethods = "PUT,GET,HEAD,OPTIONS",
            AllowedHeaders = "*",
            ExposedHeaders = "ETag",
            MaxAgeInSeconds = 3600,
        });
        await service.SetPropertiesAsync(properties, ct);
    }
}
