using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Mobile.Models;
using PoRedoMedia.Mobile.Services;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoRedoMedia.IntegrationTests;

/// <summary>
/// The phone's API client, compiled into this project from the Mobile sources, driven against the
/// real app on a real socket: cookie sign-in, direct-to-storage upload, a run, and the download.
/// </summary>
[Collection(AzuriteCollection.Name)]
public sealed class MobileClientTests(AzuriteFixture azurite)
{
    [DockerFact]
    public async Task The_phone_signs_in_uploads_a_photo_runs_a_meme_and_downloads_the_result()
    {
        using var factory = new AppFactory(azurite.ConnectionString, settings: new() { ["Auth:EnableFakeAuth"] = "false" });
        factory.UseKestrel(0);
        factory.StartServer();
        var address = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");
        var phone = new MobileApiClient(() => address);

        Assert.True(await phone.PingAsync());
        Assert.True(await phone.EnsureAuthenticatedAsync());

        using var image = new Image<Rgba32>(640, 480, new Rgba32(20, 40, 160));
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        var source = await phone.UploadAsync(new ImageCaptureResult("beach.png", "image/png", png.ToArray(), "", png.Length));

        var steps = new List<RunStatus>();
        var run = await phone.RunAsync(source.Id, [MediaFunction.MemeCaption], null, r => steps.Add(r.Status));

        Assert.Equal(RunStatus.Complete, run.Status);
        Assert.Contains(RunStatus.Complete, steps);
        var output = Assert.Single(await phone.ListGalleryAsync(), m => m.Id == run.OutputIds[0]);
        Assert.Equal(source.Id, output.ParentId);
        Assert.NotNull(Image.Identify(await phone.GetBytesAsync(output.Url)));

        await phone.DeleteAsync(source.Id);
        Assert.DoesNotContain(await phone.ListGalleryAsync(), m => m.Id == source.Id);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => phone.RunAsync(source.Id, [MediaFunction.MemeCaption], null));
        Assert.DoesNotContain("{", refused.Message);
    }
}
