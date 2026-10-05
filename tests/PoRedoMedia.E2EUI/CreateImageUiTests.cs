using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class CreateImageUiTests : UiTestBase
{
    // A valid 64x64 PNG is drawn in the browser, so the test needs no image file on disk.
    private static async Task<byte[]> PngAsync(IPage page) => Convert.FromBase64String(await page.EvaluateAsync<string>(
        """
        () => {
            const c = document.createElement('canvas'); c.width = 320; c.height = 240;
            const g = c.getContext('2d'); g.fillStyle = '#1428a0'; g.fillRect(0, 0, 320, 240);
            return c.toDataURL('image/png').split(',')[1];
        }
        """));

    [LiveServerFact]
    public async Task Picking_an_image_ticking_meme_caption_and_running_shows_the_result()
    {
        var page = await SignedInPageAsync("/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Choose a photo or a video" })).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await page.SetInputFilesAsync("input[type=file]", new FilePayload { Name = "beach.png", MimeType = "image/png", Buffer = await PngAsync(page) });

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What should happen to this image?" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Next" })).ToBeDisabledAsync();
        await page.Locator("[data-fn=MemeCaption] input").CheckAsync();
        await Assertions.Expect(page.Locator("[data-fn=BulkStyles] input")).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();

        await Assertions.Expect(page.GetByText("beach.png → Meme caption")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run" }).ClickAsync();

        await Assertions.Expect(page.GetByText("Done. The results are in your gallery.")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Img, new() { Name = "Meme caption · beach" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("9 of 10 runs left today")).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Open the gallery" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Meme caption · beach" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "beach.png" })).ToBeVisibleAsync();

    }
}
