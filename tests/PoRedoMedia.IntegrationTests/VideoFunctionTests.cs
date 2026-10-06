using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.IntegrationTests;

/// <summary>The video functions run through the real API, the real ffmpeg and mock AI.</summary>
[Collection(AzuriteCollection.Name)]
public sealed class VideoFunctionTests(AzuriteFixture azurite) : IDisposable
{
    private readonly CountingDirector _director = new();
    private AppFactory? _factory;

    private AppFactory Factory => _factory ??= new AppFactory(azurite.ConnectionString, services =>
    {
        // Wraps the mock director so a test can tell whether it was asked at all.
        var mock = services.Single(d => d.ServiceType == typeof(IDirectorService));
        services.Remove(mock);
        services.AddSingleton<IDirectorService>(s =>
        {
            _director.Inner = (IDirectorService)ActivatorUtilities.CreateInstance(s, mock.ImplementationType!);
            return _director;
        });
    });

    public void Dispose() => _factory?.Dispose();

    private static readonly FFmpegProcess Ffmpeg = new(new ConfigurationBuilder().Build(), NullLogger<FFmpegProcess>.Instance);

    private static async Task<byte[]> MakeAsync(string ffmpegInputAndOptions, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"poredomedia-{Guid.NewGuid():N}{extension}");
        try
        {
            Assert.Equal(0, await Ffmpeg.RunAsync($"-y {ffmpegInputAndOptions} \"{path}\"", "test", default));
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A four-second clip with a picture and a tone, stored as a ready gallery item.</summary>
    private async Task<MediaItem> AddVideoAsync(string user)
    {
        var clip = await MakeAsync(
            "-f lavfi -i testsrc=duration=4:size=320x240:rate=10 -f lavfi -i sine=frequency=440:duration=4 -pix_fmt yuv420p -shortest", ".mp4");
        var item = new MediaItem
        {
            Owner = new UserId(user), Id = MediaId.New(), Kind = MediaKind.Video, Status = MediaStatus.Ready, Origin = "Upload",
            Title = "party.mp4", ContentType = "video/mp4", Extension = ".mp4", DurationSeconds = 4, CreatedAt = DateTimeOffset.UtcNow,
        };
        await Factory.Services.GetRequiredService<BlobStorageService>().UploadAsync(item.SourcePath, clip, "video/mp4");
        await Factory.Services.GetRequiredService<IMediaRepository>().SaveAsync(item);
        return item;
    }

    /// <summary>Puts one short sound in the library, so Meme-ify has something to place.</summary>
    private async Task SeedSoundAsync()
    {
        var repository = Factory.Services.GetRequiredService<ISoundAssetRepository>();
        if ((await repository.LoadAllAsync()).Count > 0)
            return;

        var sound = new SoundAsset { DisplayName = "Test boom", BlobUrl = $"sounds/{Guid.NewGuid()}.mp3", DurationMs = 300, ActionVectorTags = ["explosion", "surprise", "fall"] };
        await Factory.Services.GetRequiredService<BlobStorageService>().UploadAsync(
            sound.BlobPath, await MakeAsync("-f lavfi -i sine=frequency=220:duration=0.3 -c:a libmp3lame", ".mp3"), "audio/mpeg");
        await repository.AddSoundAsync(sound);
    }

    private static async Task<RunDto> RunAsync(HttpClient client, MediaItem source, MediaFunction[] functions, Dictionary<string, string>? options = null)
    {
        var response = await client.PostAsJsonAsync("/api/runs", new RunRequest(source.Id.Value, functions, options), WireJson.Default.RunRequest);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var run = (await response.Content.ReadFromJsonAsync(WireJson.Default.RunDto))!;
        for (var attempt = 0; attempt < 600 && run.Status is RunStatus.Queued or RunStatus.Running; attempt++)
        {
            await Task.Delay(100);
            run = (await client.GetFromJsonAsync($"/api/runs/{run.Id}", WireJson.Default.RunDto))!;
        }

        return run;
    }

    private async Task<(double Duration, int AudioStreams)> ProbeAsync(MediaDto video)
    {
        var path = Path.Combine(Path.GetTempPath(), $"poredomedia-out-{Guid.NewGuid():N}.mp4");
        try
        {
            await Factory.Services.GetRequiredService<BlobStorageService>()
                .DownloadToFileAsync(MediaBlobPaths.Source(MediaId.From(video.Id), ".mp4"), path);
            return (await Ffmpeg.DurationSecondsAsync(path, default), await Ffmpeg.HasAudioStreamAsync(path, default) ? 1 : 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [DockerFact]
    public async Task All_three_video_functions_in_one_run_make_one_video_with_its_roast_text_and_captions_file()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var client = await Factory.SignedInAsync(user);
        await SeedSoundAsync();
        var source = await AddVideoAsync(user);

        var run = await RunAsync(client, source, [MediaFunction.Captions, MediaFunction.VideoRoast, MediaFunction.Memeify],
            new() { [RunOptions.VideoPersona] = "sitcom", [RunOptions.VideoAspect] = "1:1" });

        Assert.True(run.Status == RunStatus.Complete, run.Error);
        var gallery = (await client.GetFromJsonAsync("/api/media", WireJson.Default.ListMediaDto))!;
        var video = gallery.Single(m => m.Id == Assert.Single(run.OutputIds));
        Assert.Equal((MediaKind.Video, "Memeify", source.Id.Value, "Mock title · party"), (video.Kind, video.Origin, video.ParentId, video.Title));
        Assert.Contains("Mock insult one.", video.Text);
        Assert.Equal(1, _director.Calls);

        var (duration, audioStreams) = await ProbeAsync(video);
        Assert.InRange(duration, 3.5, 4.5);
        Assert.Equal(1, audioStreams);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(video.ThumbUrl)).StatusCode);

        var srt = await client.GetStringAsync($"/api/media/{video.Id}/captions.srt");
        Assert.Contains("This is a mock transcript.", srt);
        Assert.Contains("-->", srt);
    }

    [DockerFact]
    public async Task Captions_alone_render_without_asking_the_director_and_a_second_run_reuses_the_analysis()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var client = await Factory.SignedInAsync(user);
        var source = await AddVideoAsync(user);

        var first = await RunAsync(client, source, [MediaFunction.Captions]);
        var second = await RunAsync(client, source, [MediaFunction.Captions]);

        Assert.True(first.Status == RunStatus.Complete, first.Error);
        Assert.True(second.Status == RunStatus.Complete, second.Error);
        Assert.Equal(0, _director.Calls);
        var gallery = (await client.GetFromJsonAsync("/api/media", WireJson.Default.ListMediaDto))!;
        Assert.Equal("Captions", gallery.Single(m => m.Id == first.OutputIds[0]).Origin);
        // A video with no captions burned in has no captions file.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/media/{source.Id}/captions.srt")).StatusCode);
    }

    [DockerFact]
    public async Task Frames_are_described_once_and_kept_with_the_video()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var client = await Factory.SignedInAsync(user);
        var source = await AddVideoAsync(user);
        var frame = "data:image/jpeg;base64," + Convert.ToBase64String(
            await MakeAsync("-f lavfi -i testsrc=duration=1:size=64x64:rate=1 -frames:v 1", ".jpg"));
        var request = new FrameUploadRequest([frame, frame], [0.5, 3.0]);

        var first = await (await client.PostAsJsonAsync($"/api/media/{source.Id}/frames", request, WireJson.Default.FrameUploadRequest))
            .Content.ReadFromJsonAsync(WireJson.Default.FramesResult);
        var again = await (await client.PostAsJsonAsync($"/api/media/{source.Id}/frames", request, WireJson.Default.FrameUploadRequest))
            .Content.ReadFromJsonAsync(WireJson.Default.FramesResult);

        Assert.Equal(2, first!.FramesStored);
        Assert.True(first.MomentsFound > 0);
        Assert.Equal((0, first.MomentsFound), (again!.FramesStored, again.MomentsFound));
        var stranger = await Factory.SignedInAsync($"dev|{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.PostAsJsonAsync($"/api/media/{source.Id}/frames", request, WireJson.Default.FrameUploadRequest)).StatusCode);
    }

    private sealed class CountingDirector : IDirectorService
    {
        public IDirectorService Inner { get; set; } = null!;
        public int Calls { get; private set; }

        public Task<DirectedScript> DirectAsync(
            SceneLabel[] visionLabels, IReadOnlyList<SoundAsset> topCandidates, MediaId mediaId, bool hasRealVisionData = false,
            DirectorContext? context = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Inner.DirectAsync(visionLabels, topCandidates, mediaId, hasRealVisionData, context, cancellationToken);
        }
    }
}
