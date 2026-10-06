using System.Diagnostics;
using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class CreateVideoUiTests : UiTestBase
{
    /// <summary>A three-second clip with a picture and a tone, made with the local ffmpeg.</summary>
    private static async Task<byte[]> ClipAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"poredomedia-e2e-{Guid.NewGuid():N}.mp4");
        try
        {
            using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
                $"-y -f lavfi -i testsrc=duration=3:size=320x240:rate=10 -f lavfi -i sine=frequency=440:duration=3 -pix_fmt yuv420p -shortest \"{path}\"")
            {
                RedirectStandardError = true,
            })!;
            await ffmpeg.StandardError.ReadToEndAsync();
            await ffmpeg.WaitForExitAsync();
            Assert.Equal(0, ffmpeg.ExitCode);
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [LiveServerFact]
    public async Task Picking_a_video_ticking_roast_and_captions_and_running_shows_one_video_with_its_extras()
    {
        var page = await SignedInPageAsync("/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Choose a photo or a video" })).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await page.SetInputFilesAsync("input[type=file]", new FilePayload { Name = "party.mp4", MimeType = "video/mp4", Buffer = await ClipAsync() });

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What should happen to this video?" })).ToBeVisibleAsync(new() { Timeout = 60_000 });
        await Assertions.Expect(page.Locator("[data-fn=Restyle]")).ToHaveCountAsync(0);
        await page.Locator("[data-fn=VideoRoast] input").CheckAsync();
        await page.Locator("[data-fn=Captions] input").CheckAsync();
        await Assertions.Expect(page.GetByText("Comic voice")).ToBeVisibleAsync();
        await page.GetByText("Square 1:1").ClickAsync();

        await Assertions.Expect(page.GetByText("party.mp4 → Insult roast, then Auto-captions")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();

        await Assertions.Expect(page.GetByText("Done. The results are in your gallery.")).ToBeVisibleAsync(new() { Timeout = 90_000 });
        var result = page.Locator("figure[data-origin=VideoRoast]");
        await Assertions.Expect(result.Locator("video")).ToHaveCountAsync(1);
        await Assertions.Expect(result).ToContainTextAsync("Mock insult one.");
        await Assertions.Expect(result.GetByRole(AriaRole.Link, new() { Name = "Download the captions (SRT)" })).ToBeVisibleAsync();

        // The result is a square video, as asked.
        var shape = await result.Locator("video").EvaluateAsync<double[]>(
            "v => new Promise(r => v.readyState >= 1 ? r([v.videoWidth, v.videoHeight]) : v.addEventListener('loadedmetadata', () => r([v.videoWidth, v.videoHeight])))");
        Assert.Equal(shape[0], shape[1]);
    }
}
