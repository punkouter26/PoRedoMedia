using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoRedoMedia.IntegrationTests;

[Collection(AzuriteCollection.Name)]
public sealed class MediaEndpointsTests(AzuriteFixture azurite) : IDisposable
{
    private readonly Lazy<AppFactory> _factory = new(() => new AppFactory(azurite.ConnectionString));
    private static readonly HttpClient Storage = new();

    public void Dispose()
    {
        if (_factory.IsValueCreated)
            _factory.Value.Dispose();
    }

    private static byte[] Png()
    {
        using var image = new Image<Rgba32>(64, 48, new Rgba32(200, 30, 30));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static async Task<byte[]> ClipAsync(int seconds)
    {
        var ffmpeg = new FFmpegProcess(new ConfigurationBuilder().Build(), NullLogger<FFmpegProcess>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"poredomedia-{Guid.NewGuid():N}.mp4");
        try
        {
            // One tiny frame a second keeps even a ten-minute clip to a few kilobytes.
            var exit = await ffmpeg.RunAsync($"-y -f lavfi -i testsrc=duration={seconds}:size=32x32:rate=1 -pix_fmt yuv420p \"{path}\"", "test", default);
            Assert.Equal(0, exit);
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<HttpResponseMessage> UploadAndConfirmAsync(HttpClient client, string fileName, byte[] bytes)
    {
        var ticketResponse = await client.PostAsJsonAsync("/api/media/sas", new UploadRequest(fileName, bytes.Length));
        ticketResponse.EnsureSuccessStatusCode();
        var ticket = (await ticketResponse.Content.ReadFromJsonAsync(WireJson.Default.UploadTicket))!;

        var put = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl) { Content = new ByteArrayContent(bytes) };
        put.Headers.Add("x-ms-blob-type", "BlockBlob");
        (await Storage.SendAsync(put)).EnsureSuccessStatusCode();

        return await client.PostAsync($"/api/media/{ticket.Id}/confirm", null);
    }

    private static async Task<List<MediaDto>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync("/api/media", WireJson.Default.ListMediaDto))!;

    [DockerFact]
    public async Task An_image_is_uploaded_listed_served_and_deleted_whole()
    {
        var client = await _factory.Value.SignedInAsync($"dev|{Guid.NewGuid()}");

        var confirmed = await UploadAndConfirmAsync(client, "beach.png", Png());
        var media = (await confirmed.Content.ReadFromJsonAsync(WireJson.Default.MediaDto))!;

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal((MediaKind.Image, "beach.png", "Upload"), (media.Kind, media.Title, media.Origin));
        Assert.Equal(media.Id, Assert.Single(await ListAsync(client)).Id);

        foreach (var url in new[] { media.Url, media.ThumbUrl })
        {
            var redirect = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
            Assert.NotEmpty(await Storage.GetByteArrayAsync(redirect.Headers.Location));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/media/{media.Id}")).StatusCode);
        Assert.Empty(await ListAsync(client));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(media.Url)).StatusCode);
    }

    [DockerFact]
    public async Task Writing_to_the_upload_link_again_after_the_check_does_not_change_what_is_stored()
    {
        var client = await _factory.Value.SignedInAsync($"dev|{Guid.NewGuid()}");
        var ticket = (await (await client.PostAsJsonAsync("/api/media/sas", new UploadRequest("a.png", 10)))
            .Content.ReadFromJsonAsync(WireJson.Default.UploadTicket))!;

        async Task PutAsync(byte[] bytes, string contentType)
        {
            var put = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl) { Content = new ByteArrayContent(bytes) };
            put.Content.Headers.ContentType = new(contentType);
            put.Headers.Add("x-ms-blob-type", "BlockBlob");
            (await Storage.SendAsync(put)).EnsureSuccessStatusCode();
        }

        var png = Png();
        await PutAsync(png, "text/html");
        (await client.PostAsync($"/api/media/{ticket.Id}/confirm", null)).EnsureSuccessStatusCode();
        await PutAsync("<script>alert(1)</script>"u8.ToArray(), "text/html");

        var redirect = await client.GetAsync($"/api/media/{ticket.Id}/content");
        var served = await Storage.GetAsync(redirect.Headers.Location);
        Assert.Equal(png, await served.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
    }

    [DockerFact]
    public async Task An_image_with_more_pixels_than_the_limit_is_refused_before_it_is_decoded()
    {
        var client = await _factory.Value.SignedInAsync($"dev|{Guid.NewGuid()}");
        // One flat colour compresses to a few kilobytes however many pixels it declares.
        using var huge = new Image<L8>(8000, 5001);
        using var stream = new MemoryStream();
        huge.SaveAsPng(stream);

        var response = await UploadAndConfirmAsync(client, "huge.png", stream.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ListAsync(client));
    }

    [DockerFact]
    public async Task An_uploaded_sound_belongs_to_its_uploader_alone()
    {
        var owner = await _factory.Value.SignedInAsync($"dev|{Guid.NewGuid()}");
        var other = await _factory.Value.SignedInAsync($"dev|{Guid.NewGuid()}");
        var name = $"honk-{Guid.NewGuid():N}";
                // Starts like an MP3: the upload is checked by its first bytes, not by its name.
        var mp3 = new byte[2048];
        "ID3"u8.CopyTo(mp3);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(mp3), "file", "honk.mp3" } };

        var created = await owner.PostAsync($"/api/sounds/upload?displayName={name}", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // A file that is only named like a sound is refused.
        using var fake = new MultipartFormDataContent { { new ByteArrayContent(new byte[2048]), "file", "fake.mp3" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/sounds/upload", fake)).StatusCode);

        Assert.Contains((await owner.GetFromJsonAsync("/api/sounds", WireJson.Default.ListSoundAssetDto))!, s => s.DisplayName == name);
        Assert.DoesNotContain((await other.GetFromJsonAsync("/api/sounds", WireJson.Default.ListSoundAssetDto))!, s => s.DisplayName == name);
        // The sound is served by a redirect to a short-lived storage link.
        Assert.Equal(HttpStatusCode.Redirect, (await owner.GetAsync(created.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(created.Headers.Location)).StatusCode);
    }

    [DockerFact]
    public async Task A_file_that_only_claims_to_be_an_image_is_refused_and_removed()
    {
        var client = await _factory.Value.SignedInAsync($"dev|{Guid.NewGuid()}");

        var confirmed = await UploadAndConfirmAsync(client, "fake.png", "this is not a picture"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, confirmed.StatusCode);
        Assert.Empty(await ListAsync(client));
    }

    [DockerFact]
    public async Task A_video_gets_its_measured_duration_and_a_thumbnail()
    {
        var client = await _factory.Value.SignedInAsync($"dev|{Guid.NewGuid()}");

        var confirmed = await UploadAndConfirmAsync(client, "clip.mp4", await ClipAsync(2));
        var media = (await confirmed.Content.ReadFromJsonAsync(WireJson.Default.MediaDto))!;

        Assert.Equal(MediaKind.Video, media.Kind);
        Assert.InRange(media.DurationSeconds!.Value, 1.5, 2.5);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(media.ThumbUrl)).StatusCode);
    }

    [DockerFact]
    public async Task A_video_longer_than_a_minute_is_refused()
    {
        var client = await _factory.Value.SignedInAsync($"dev|{Guid.NewGuid()}");

        var confirmed = await UploadAndConfirmAsync(client, "long.mp4", await ClipAsync(65));

        Assert.Equal(HttpStatusCode.BadRequest, confirmed.StatusCode);
        Assert.Contains("1 minute", await confirmed.Content.ReadAsStringAsync());
    }

    [DockerFact]
    public async Task One_user_cannot_confirm_read_or_delete_anothers_item()
    {
        var alice = await _factory.Value.SignedInAsync($"dev|alice-{Guid.NewGuid()}");
        var bob = await _factory.Value.SignedInAsync($"dev|bob-{Guid.NewGuid()}");
        var media = (await (await UploadAndConfirmAsync(alice, "mine.png", Png())).Content.ReadFromJsonAsync(WireJson.Default.MediaDto))!;

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(media.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/media/{media.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/media/{media.Id}/confirm", null)).StatusCode);
        Assert.Single(await ListAsync(alice));
    }

    [DockerFact]
    public async Task Pin_and_title_changes_are_kept()
    {
        var client = await _factory.Value.SignedInAsync($"dev|{Guid.NewGuid()}");
        var media = (await (await UploadAndConfirmAsync(client, "a.png", Png())).Content.ReadFromJsonAsync(WireJson.Default.MediaDto))!;

        (await client.PutAsJsonAsync($"/api/media/{media.Id}", new MediaUpdateRequest("Renamed", true))).EnsureSuccessStatusCode();

        var updated = Assert.Single(await ListAsync(client));
        Assert.Equal(("Renamed", true), (updated.Title, updated.Pinned));
    }

    [DockerFact]
    public async Task An_unsupported_type_is_refused_before_any_upload_link_is_issued()
    {
        var client = await _factory.Value.SignedInAsync($"dev|{Guid.NewGuid()}");

        var response = await client.PostAsJsonAsync("/api/media/sas", new UploadRequest("virus.exe", 10));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ListAsync(client));
    }

    [DockerFact]
    public async Task A_shared_item_is_public_until_sharing_stops_or_it_is_deleted()
    {
        var owner = await _factory.Value.SignedInAsync($"dev|{Guid.NewGuid()}");
        var visitor = _factory.Value.CreateClient(new() { AllowAutoRedirect = false });
        var media = (await (await UploadAndConfirmAsync(owner, "<b>beach.png", Png())).Content.ReadFromJsonAsync(WireJson.Default.MediaDto))!;

        var link = (await (await owner.PostAsync($"/api/media/{media.Id}/share", null)).Content.ReadFromJsonAsync(WireJson.Default.ShareLinkDto))!;
        var again = (await (await owner.PostAsync($"/api/media/{media.Id}/share", null)).Content.ReadFromJsonAsync(WireJson.Default.ShareLinkDto))!;
        var path = new Uri(link.Url).AbsolutePath;

        Assert.Equal(link.Url, again.Url);
        var page = await visitor.GetAsync(path);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("og:image", html);
        // The title came from a file name the user chose; it must arrive as text, not markup.
        Assert.Contains("&lt;b&gt;beach.png", html);
        Assert.DoesNotContain("<b>beach", html);
        var content = await visitor.GetAsync(path + "/content");
        Assert.Equal(HttpStatusCode.Redirect, content.StatusCode);
        Assert.NotEmpty(await Storage.GetByteArrayAsync(content.Headers.Location));
        Assert.True(Assert.Single(await ListAsync(owner)).Shared);

        (await owner.DeleteAsync($"/api/media/{media.Id}/share")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(path + "/content")).StatusCode);

        var second = (await (await owner.PostAsync($"/api/media/{media.Id}/share", null)).Content.ReadFromJsonAsync(WireJson.Default.ShareLinkDto))!;
        Assert.NotEqual(link.Url, second.Url);
        (await owner.DeleteAsync($"/api/media/{media.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(new Uri(second.Url).AbsolutePath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/v/not-a-token")).StatusCode);
    }
}
