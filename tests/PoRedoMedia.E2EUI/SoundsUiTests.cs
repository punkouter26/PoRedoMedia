using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class SoundsUiTests : UiTestBase
{
    /// <summary>A minimal valid WAV: a tenth of a second of silence.</summary>
    private static byte[] Wav()
    {
        const int sampleRate = 8000, samples = 800;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
        return stream.ToArray();
    }

    [LiveServerFact]
    public async Task An_uploaded_sound_is_listed_can_be_starred_and_found_by_search()
    {
        var page = await SignedInPageAsync("/sounds");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sounds" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        var name = $"e2e-{Guid.NewGuid():N}"[..14];

        await page.SetInputFilesAsync("input[type=file]", new FilePayload { Name = name + ".wav", MimeType = "audio/wav", Buffer = Wav() });
        await page.GetByPlaceholder("Search names and tags").FillAsync(name);

        var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = name });
        await Assertions.Expect(row).ToHaveCountAsync(1, new() { Timeout = 30_000 });
        await row.GetByRole(AriaRole.Button, new() { Name = $"Star {name}" }).ClickAsync();
        await Assertions.Expect(row.GetByRole(AriaRole.Button, new() { Name = $"Unstar {name}" })).ToBeVisibleAsync();

        await page.Locator(".sounds-toggle .rz-switch").ClickAsync();
        await Assertions.Expect(row).ToHaveCountAsync(1);
    }
}
