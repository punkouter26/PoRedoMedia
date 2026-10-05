using System.Text;
using Microsoft.Extensions.Configuration;
using PoRedoMedia.Api.Common;

namespace PoRedoMedia.IntegrationTests;

[Collection(AzuriteCollection.Name)]
public sealed class BlobStorageTests(AzuriteFixture azurite)
{
    private BlobStorageService Blobs() => new(Clients());

    private StorageClients Clients() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:ConnectionString"] = azurite.ConnectionString })
        .Build());

    [DockerFact]
    public async Task A_blob_round_trips_and_a_prefix_delete_removes_everything_under_it()
    {
        var blobs = Blobs();
        var prefix = $"media/{Guid.NewGuid()}/";
        await blobs.UploadAsync(prefix + "source.txt", new MemoryStream("hello"u8.ToArray()), "text/plain");
        await blobs.UploadAsync(prefix + "frames/0.txt", new MemoryStream("frame"u8.ToArray()), "text/plain");

        Assert.True(await blobs.ExistsAsync(prefix + "source.txt"));
        Assert.Equal("hello", Encoding.UTF8.GetString(await blobs.ReadAllBytesAsync(prefix + "source.txt")));

        await blobs.DeletePrefixAsync(prefix);

        Assert.False(await blobs.ExistsAsync(prefix + "source.txt"));
        Assert.False(await blobs.ExistsAsync(prefix + "frames/0.txt"));
    }

    [DockerFact]
    public async Task An_upload_link_accepts_one_put_and_a_read_link_serves_it_back()
    {
        var clients = Clients();
        var path = $"media/{Guid.NewGuid()}/source.bin";
        using var http = new HttpClient();

        var upload = await clients.CreateUploadLinkAsync(path, DateTimeOffset.UtcNow.AddMinutes(5));
        var put = new HttpRequestMessage(HttpMethod.Put, upload) { Content = new ByteArrayContent([1, 2, 3]) };
        put.Headers.Add("x-ms-blob-type", "BlockBlob");
        (await http.SendAsync(put)).EnsureSuccessStatusCode();

        var read = clients.CreateReadLink(path, TimeSpan.FromMinutes(5));
        Assert.Equal([1, 2, 3], await http.GetByteArrayAsync(read));
    }

    [Fact]
    public void Unconfigured_storage_fails_loudly_rather_than_pretending_to_save()
    {
        var clients = new StorageClients(new ConfigurationBuilder().Build());

        var error = Assert.Throws<InvalidOperationException>(() => clients.Container("media"));
        Assert.Contains("Storage:ConnectionString", error.Message);
    }
}
