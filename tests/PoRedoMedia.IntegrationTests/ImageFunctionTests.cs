using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoRedoMedia.IntegrationTests;

/// <summary>Real functions run through the real API against mock AI.</summary>
[Collection(AzuriteCollection.Name)]
public sealed class ImageFunctionTests(AzuriteFixture azurite) : IDisposable
{
    private readonly Lazy<AppFactory> _factory = new(() => new AppFactory(azurite.ConnectionString));

    public void Dispose()
    {
        if (_factory.IsValueCreated)
            _factory.Value.Dispose();
    }

    private async Task<MediaItem> AddImageAsync(string user, int width = 640, int height = 480)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(20, 40, 160));
        using var png = new MemoryStream();
        image.SaveAsPng(png);

        var services = _factory.Value.Services;
        var item = new MediaItem
        {
            Owner = new UserId(user), Id = MediaId.New(), Kind = MediaKind.Image, Status = MediaStatus.Ready, Origin = "Upload",
            Title = "beach.png", ContentType = "image/png", Extension = ".png", CreatedAt = DateTimeOffset.UtcNow,
        };
        await services.GetRequiredService<BlobStorageService>().UploadAsync(item.SourcePath, png.ToArray(), "image/png");
        await services.GetRequiredService<IMediaRepository>().SaveAsync(item);
        return item;
    }

    private static async Task<RunDto> RunAsync(HttpClient client, MediaItem source, MediaFunction[] functions, Dictionary<string, string>? options = null)
    {
        var response = await client.PostAsJsonAsync("/api/runs", new RunRequest(source.Id.Value, functions, options), WireJson.Default.RunRequest);
        response.EnsureSuccessStatusCode();
        var run = (await response.Content.ReadFromJsonAsync(WireJson.Default.RunDto))!;
        for (var attempt = 0; attempt < 150 && run.Status is RunStatus.Queued or RunStatus.Running; attempt++)
        {
            await Task.Delay(100);
            run = (await client.GetFromJsonAsync($"/api/runs/{run.Id}", WireJson.Default.RunDto))!;
        }

        return run;
    }

    [DockerTheory]
    [InlineData("ai", null)]
    [InlineData("text", "MemeCaption.top=hello there")]
    [InlineData("template", "MemeCaption.template=drake;MemeCaption.zone0=old way;MemeCaption.zone1=new way")]
    public async Task Meme_caption_saves_a_captioned_copy_made_from_the_source(string mode, string? extraOptions)
    {
        var user = $"dev|{Guid.NewGuid()}";
        var client = await _factory.Value.SignedInAsync(user);
        var source = await AddImageAsync(user);
        var options = new Dictionary<string, string> { [RunOptions.MemeMode] = mode };
        foreach (var pair in (extraOptions ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            options[pair.Split('=')[0]] = pair.Split('=')[1];

        var run = await RunAsync(client, source, [MediaFunction.MemeCaption], options);

        Assert.True(run.Status == RunStatus.Complete, run.Error);
        var gallery = (await client.GetFromJsonAsync("/api/media", WireJson.Default.ListMediaDto))!;
        var meme = Assert.Single(gallery, m => m.Id == Assert.Single(run.OutputIds));
        Assert.Equal((MediaKind.Image, "MemeCaption", source.Id.Value, "Meme caption · beach"), (meme.Kind, meme.Origin, meme.ParentId, meme.Title));

        var bytes = await _factory.Value.Services.GetRequiredService<BlobStorageService>()
            .ReadAllBytesAsync(MediaBlobPaths.Source(MediaId.From(meme.Id), ".png"));
        using var image = Image.Load<Rgba32>(bytes);
        Assert.Equal((640, 480), (image.Width, image.Height));
        var white = 0;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (var pixel in rows.GetRowSpan(y))
                    if (pixel is { R: > 240, G: > 240, B: > 240 })
                        white++;
        });
        Assert.True(white > 100, "the saved image has no caption drawn on it");
    }

    [DockerFact]
    public async Task Typed_mode_with_no_text_fails_the_run_with_a_reason_and_saves_nothing()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var client = await _factory.Value.SignedInAsync(user);
        var source = await AddImageAsync(user);

        var run = await RunAsync(client, source, [MediaFunction.MemeCaption], new() { [RunOptions.MemeMode] = "text" });

        Assert.Equal((RunStatus.Failed, "Type the top or the bottom text."), (run.Status, run.Error));
        Assert.Empty(run.OutputIds);
    }
}
