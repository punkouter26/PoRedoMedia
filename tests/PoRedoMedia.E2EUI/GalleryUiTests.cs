using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class GalleryUiTests : UiTestBase
{
    // A valid 1x1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [LiveServerFact]
    public async Task An_uploaded_image_appears_in_the_gallery_and_can_be_deleted()
    {
        var page = await SignedInPageAsync("/gallery");
        await Assertions.Expect(page.GetByText("Nothing here yet")).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await page.SetInputFilesAsync("input[type=file]", new FilePayload { Name = "beach.png", MimeType = "image/png", Buffer = Png });

        var card = page.GetByRole(AriaRole.Button, new() { Name = "beach.png" });
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Img, new() { Name = "beach.png" })).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
        await page.GetByRole(AriaRole.Alertdialog).GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();

        await Assertions.Expect(page.GetByText("Nothing here yet")).ToBeVisibleAsync();
    }

    [LiveServerFact]
    public async Task A_file_of_the_wrong_type_is_refused_with_the_reason()
    {
        var page = await SignedInPageAsync("/gallery");
        await Assertions.Expect(page.GetByText("Nothing here yet")).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await page.SetInputFilesAsync("input[type=file]", new FilePayload { Name = "notes.txt", MimeType = "text/plain", Buffer = "hello"u8.ToArray() });

        await Assertions.Expect(page.GetByText("That file type is not supported")).ToBeVisibleAsync();
    }
}
