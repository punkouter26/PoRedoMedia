using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class CaptureAndAccessibilityUiTests : UiTestBase
{
    private static readonly string Shots = Path.Combine(Path.GetTempPath(), "poredomedia-shots");

    [LiveServerFact]
    public async Task A_photo_taken_with_the_camera_becomes_the_picked_media()
    {
        var page = await SignedInPageAsync("/");
        await page.GetByRole(AriaRole.Button, new() { Name = "Use the camera" }).ClickAsync(new() { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Take the photo" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What should happen to this image?" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByText(new System.Text.RegularExpressions.Regex(@"camera-\d+\.jpg · image"))).ToBeVisibleAsync();
    }

    [LiveServerFact]
    public async Task The_app_is_installable_and_its_main_pages_have_no_WCAG_AA_violations()
    {
        Directory.CreateDirectory(Shots);
        var failures = new List<string>();

        async Task ScanAsync(IPage page, string name)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, name + ".png"), FullPage = true });
            var result = await page.RunAxe(new AxeRunOptions
            {
                RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"] },
            });
            failures.AddRange(result.Violations.Select(v => $"{name}: {v.Id} ({v.Impact}) {v.Help} -> {string.Join(" | ", v.Nodes.Take(3).Select(n => n.Html))}"));
        }

        var anonymous = await Browser.NewPageAsync();
        await anonymous.GotoAsync(BaseUrl + "/login");
        await Assertions.Expect(anonymous.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await ScanAsync(anonymous, "login");

        // Installable: the page links a manifest that names the app and offers both icon sizes.
        var manifest = await anonymous.APIRequest.GetAsync(BaseUrl + "/manifest.webmanifest");
        Assert.True(manifest.Ok);
        var json = (await manifest.JsonAsync())!.Value;
        Assert.Equal("standalone", json.GetProperty("display").GetString());
        Assert.Contains(json.GetProperty("icons").EnumerateArray(), i => i.GetProperty("sizes").GetString() == "512x512");
        Assert.Equal(1, await anonymous.Locator("link[rel=manifest]").CountAsync());

        var page = await SignedInPageAsync("/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Choose a photo or a video" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await ScanAsync(page, "create-1-media");

        await page.GetByRole(AriaRole.Button, new() { Name = "Use the camera" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Take the photo" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What should happen to this image?" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        foreach (var function in (string[])["Restyle", "MemeCaption", "RapRoast", "PhotoToVideo"])
            await page.Locator($"[data-fn={function}] input").CheckAsync();
        await ScanAsync(page, "create-2-functions");

        await page.GotoAsync(BaseUrl + "/gallery");
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("camera-") }).First.ClickAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Share", Exact = true })).ToBeVisibleAsync();
        await ScanAsync(page, "gallery");

        await page.GotoAsync(BaseUrl + "/sounds");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sounds" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.WaitForSelectorAsync(".rz-data-grid");
        await ScanAsync(page, "sounds");

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Runs the real on-device vision model in the browser, which downloads about 230 MB the first
    /// time. Opt in with E2E_LOCAL_AI=1; otherwise it is reported as skipped.
    /// </summary>
    [LocalAiFact]
    public async Task The_on_device_model_describes_the_picture_and_the_run_completes()
    {
        var page = await SignedInPageAsync("/");
        var log = new List<string>();
        page.Console += (_, m) => { if (m.Type is "error" or "warning") log.Add($"{m.Type}: {m.Text}"); };
        await page.GetByRole(AriaRole.Button, new() { Name = "Use the camera" }).ClickAsync(new() { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Take the photo" }).ClickAsync();
        await page.Locator("[data-fn=MemeCaption] input").CheckAsync(new() { Timeout = 30_000 });

        await page.Locator(".rz-dropdown").Last.ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "On this device (Florence-2)" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Estimated cost: $0.00")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run" }).ClickAsync();

        try
        {
            await Assertions.Expect(page.GetByText("Done. The results are in your gallery.")).ToBeVisibleAsync(new() { Timeout = 600_000 });
        }
        catch (Exception)
        {
            var shown = await page.Locator(".rz-alert").AllInnerTextsAsync();
            throw new Xunit.Sdk.XunitException($"On-device run did not finish. Shown: {string.Join(" | ", shown)}. Console: {string.Join(" | ", log.TakeLast(8))}");
        }
    }
}

public sealed class LocalAiFactAttribute : FactAttribute
{
    public LocalAiFactAttribute()
    {
        var live = new LiveServerFactAttribute();
        Skip = live.Skip ?? (Environment.GetEnvironmentVariable("E2E_LOCAL_AI") == "1" ? null : "Set E2E_LOCAL_AI=1 to run the on-device model (downloads about 230 MB).");
    }
}
