using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoRedoMedia.Api.Common.Ai;
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

    [DockerTheory]
    [InlineData(MediaFunction.MemeCaption, "MemeCaption.mode=text", "Type the top or the bottom text")]
    [InlineData(MediaFunction.MemeCaption, "MemeCaption.mode=template;MemeCaption.template=drake;MemeCaption.zone0=only one", "needs 2 lines")]
    [InlineData(MediaFunction.PhotoToVideo, null, "Describe what should happen")]
    [InlineData(MediaFunction.Restyle, "Restyle.style=no-such-style", "Pick a style")]
    public async Task Options_a_step_would_refuse_are_refused_up_front_and_cost_nothing(MediaFunction function, string? options, string reason)
    {
        var user = $"dev|{Guid.NewGuid()}";
        var client = await _factory.Value.SignedInAsync(user);
        var source = await AddImageAsync(user);
        var sent = (options ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(o => o.Split('=')).ToDictionary(o => o[0], o => o[1]);

        var response = await client.PostAsJsonAsync("/api/runs", new RunRequest(source.Id.Value, [function], sent), WireJson.Default.RunRequest);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, (await client.GetFromJsonAsync("/api/quota", WireJson.Default.QuotaStatusDto))!.Used);
    }

    [DockerFact]
    public async Task Restyle_then_meme_caption_makes_two_images_and_the_meme_is_drawn_on_the_restyled_one()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var client = await _factory.Value.SignedInAsync(user);
        var source = await AddImageAsync(user, width: 800, height: 400);

        var run = await RunAsync(client, source, [MediaFunction.MemeCaption, MediaFunction.Restyle], new() { [RunOptions.RestyleStyle] = "vangogh" });

        Assert.True(run.Status == RunStatus.Complete, run.Error);
        var gallery = (await client.GetFromJsonAsync("/api/media", WireJson.Default.ListMediaDto))!;
        var restyled = gallery.Single(m => m.Id == run.OutputIds[0]);
        var meme = gallery.Single(m => m.Id == run.OutputIds[1]);
        Assert.Equal(("Restyle", source.Id.Value), (restyled.Origin, restyled.ParentId));
        Assert.Equal(("MemeCaption", restyled.Id), (meme.Origin, meme.ParentId));

        // The mock generator draws a new picture in the source's 2:1 shape, as the real one is asked to.
        var bytes = await _factory.Value.Services.GetRequiredService<BlobStorageService>()
            .ReadAllBytesAsync(MediaBlobPaths.Source(MediaId.From(restyled.Id), ".png"));
        var info = Image.Identify(bytes);
        Assert.Equal(2.0, (double)info.Width / info.Height, precision: 1);
    }

    [DockerFact]
    public async Task Bulk_styles_draws_one_picture_per_prompt_sent_or_else_per_saved_prompt()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var client = await _factory.Value.SignedInAsync(user);
        var source = await AddImageAsync(user);

        var sent = await RunAsync(client, source, [MediaFunction.BulkStyles], new()
        {
            [RunOptions.BulkPrompt(0)] = "The Old West Wanted Poster: <PERSON> on a handbill",
            [RunOptions.BulkPrompt(1)] = "Comic: <PERSON> as a comic panel",
            [RunOptions.BulkPrompt(2)] = "Vase: <PERSON> on an amphora",
        });
        (await client.PutAsJsonAsync("/api/bulk-prompts", new List<string> { "Mine: only this one", " " }, WireJson.Default.ListString)).EnsureSuccessStatusCode();
        var saved = await RunAsync(client, source, [MediaFunction.BulkStyles]);

        Assert.True(sent.Status == RunStatus.Complete, sent.Error);
        Assert.Equal(3, sent.OutputIds.Length);
        Assert.Single(saved.OutputIds);
        var gallery = (await client.GetFromJsonAsync("/api/media", WireJson.Default.ListMediaDto))!;
        Assert.Equal(
            ["Comic · beach", "Mine · beach", "The Old West Wanted Poster · beach", "Vase · beach"],
            gallery.Where(m => m.Origin == "BulkStyles").Select(m => m.Title).Order());
        Assert.All(gallery.Where(m => m.Origin == "BulkStyles"), m => Assert.Equal(source.Id.Value, m.ParentId));
        Assert.Equal(["Mine: only this one"], await client.GetFromJsonAsync("/api/bulk-prompts", WireJson.Default.ListString));
    }

    [DockerFact]
    public async Task A_description_written_on_the_device_means_the_server_never_looks_at_the_picture()
    {
        var vision = new CountingVision();
        using var factory = new AppFactory(azurite.ConnectionString, services =>
        {
            services.RemoveAll<IVisionServiceRouter>();
            services.AddSingleton<IVisionServiceRouter>(vision);
        });
        var user = $"dev|{Guid.NewGuid()}";
        var client = await factory.SignedInAsync(user);
        using var image = new Image<Rgba32>(64, 64);
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        var source = new MediaItem
        {
            Owner = new UserId(user), Id = MediaId.New(), Kind = MediaKind.Image, Status = MediaStatus.Ready, Origin = "Upload",
            Title = "a.png", ContentType = "image/png", Extension = ".png", CreatedAt = DateTimeOffset.UtcNow,
        };
        await factory.Services.GetRequiredService<BlobStorageService>().UploadAsync(source.SourcePath, png.ToArray(), "image/png");
        await factory.Services.GetRequiredService<IMediaRepository>().SaveAsync(source);
        var onDevice = new Dictionary<string, string> { [RunOptions.VisionModel] = "browser:florence2-base", [RunOptions.VisionDescription] = "A cat in a box" };

        var described = await RunAsync(client, source, [MediaFunction.Restyle, MediaFunction.MemeCaption, MediaFunction.RapRoast], onDevice);
        var notDescribed = await RunAsync(client, source, [MediaFunction.MemeCaption], new() { [RunOptions.VisionModel] = "browser:florence2-base" });

        Assert.True(described.Status == RunStatus.Complete, described.Error);
        Assert.Equal(0, vision.Calls);
        // A browser model with nothing from the browser is refused, not replaced by a paid one.
        Assert.Equal(RunStatus.Failed, notDescribed.Status);
        Assert.Equal(1, vision.Resolves);
    }

    private sealed class CountingVision : IVisionServiceRouter, IVisionService
    {
        public int Calls { get; private set; }
        public int Resolves { get; private set; }

        public IVisionService Resolve(string? modelId)
        {
            Resolves++;
            return modelId is null ? this : throw new RunStepException("The picked vision model is not available on this server.");
        }

        public Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new VisionResult("seen by the server", [], 1));
        }
    }

    [DockerFact]
    public async Task Rap_roast_saves_the_track_with_its_lyrics()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var client = await _factory.Value.SignedInAsync(user);
        var source = await AddImageAsync(user);

        var run = await RunAsync(client, source, [MediaFunction.RapRoast], new() { [RunOptions.RoastStyle] = "StandUp" });

        Assert.True(run.Status == RunStatus.Complete, run.Error);
        var roast = (await client.GetFromJsonAsync("/api/media", WireJson.Default.ListMediaDto))!.Single(m => m.Id == Assert.Single(run.OutputIds));
        Assert.Equal((MediaKind.Audio, "RapRoast", source.Id.Value, "audio/mpeg"), (roast.Kind, roast.Origin, roast.ParentId, roast.ContentType));
        Assert.False(string.IsNullOrWhiteSpace(roast.Text));
        // Mock mode has no chat model, so the lyrics are the built-in ones and the run says so.
        Assert.NotEmpty(run.Notes);
    }

    [DockerFact]
    public async Task All_four_stackable_functions_in_one_run_make_two_images_a_track_and_a_clip_from_the_final_image()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var client = await _factory.Value.SignedInAsync(user);
        var source = await AddImageAsync(user);

        var run = await RunAsync(
            client, source, [MediaFunction.PhotoToVideo, MediaFunction.RapRoast, MediaFunction.MemeCaption, MediaFunction.Restyle],
            new() { [RunOptions.VideoPrompt] = "The picture slowly zooms in" });

        Assert.True(run.Status == RunStatus.Complete, run.Error);
        var gallery = (await client.GetFromJsonAsync("/api/media", WireJson.Default.ListMediaDto))!;
        var made = run.OutputIds.Select(id => gallery.Single(m => m.Id == id)).ToList();
        Assert.Equal(["Restyle", "MemeCaption", "RapRoast", "PhotoToVideo"], made.Select(m => m.Origin));
        Assert.Equal([MediaKind.Image, MediaKind.Image, MediaKind.Audio, MediaKind.Video], made.Select(m => m.Kind));
        // The roast and the clip are both made from the captioned image, the last one in the chain.
        Assert.Equal([made[1].Id, made[1].Id], made.Skip(2).Select(m => m.ParentId!.Value));
        Assert.InRange(made[3].DurationSeconds!.Value, 1.5, 2.5);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, (await client.GetAsync(made[3].ThumbUrl)).StatusCode);
    }

    [DockerFact]
    public async Task A_roast_the_music_provider_refuses_twice_ends_with_the_lyrics_in_a_note_and_no_audio()
    {
        var refusing = new RefusingMusic();
        using var factory = new AppFactory(azurite.ConnectionString, services =>
        {
            services.RemoveAll<IMusicGenerationService>();
            services.AddSingleton<IMusicGenerationService>(refusing);
        });
        var user = $"dev|{Guid.NewGuid()}";
        var client = await factory.SignedInAsync(user);
        var services = factory.Services;
        using var image = new Image<Rgba32>(64, 64);
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        var source = new MediaItem
        {
            Owner = new UserId(user), Id = MediaId.New(), Kind = MediaKind.Image, Status = MediaStatus.Ready, Origin = "Upload",
            Title = "a.png", ContentType = "image/png", Extension = ".png", CreatedAt = DateTimeOffset.UtcNow,
        };
        await services.GetRequiredService<BlobStorageService>().UploadAsync(source.SourcePath, png.ToArray(), "image/png");
        await services.GetRequiredService<IMediaRepository>().SaveAsync(source);

        var run = await RunAsync(client, source, [MediaFunction.RapRoast]);

        Assert.Equal((RunStatus.Complete, 2), (run.Status, refusing.Calls));
        Assert.Empty(run.OutputIds);
        Assert.Contains(run.Notes, n => n.Contains("declined to perform the roast"));
    }

    private sealed class RefusingMusic : IMusicGenerationService
    {
        public int Calls { get; private set; }
        public bool IsConfigured => true;

        public Task<MusicGenerationResult> GenerateAsync(string lyrics, string stylePrompt, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(MusicGenerationResult.FromRefusal(1, "blocked"));
        }
    }
}
