using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

/// <summary>One headless browser per test class, plus the sign-in every signed-in test starts with.</summary>
public abstract class UiTestBase : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    protected IBrowser Browser { get; private set; } = null!;
    protected static string BaseUrl => LiveServerFactAttribute.BaseUrl;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = Environment.GetEnvironmentVariable("HEADED") != "1",
            // A synthetic camera, granted without a prompt, so the camera path can be tested.
            Args = ["--use-fake-device-for-media-stream", "--use-fake-ui-for-media-stream"],
        });
    }

    public async Task DisposeAsync()
    {
        await Browser.DisposeAsync();
        _playwright.Dispose();
    }

    /// <summary>A page signed in as a fresh developer user, so tests never see each other's media.</summary>
    /// <param name="switches">Settings → Advanced switches to turn on first, by key (Client/Services/AppSettings).</param>
    protected async Task<IPage> SignedInPageAsync(string path, params string[] switches)
    {
        var page = await Browser.NewPageAsync();
        if (switches.Length > 0)
            await page.AddInitScriptAsync(string.Concat(switches.Select(key => $"localStorage.setItem('{key}', 'on');")));
        var user = Uri.EscapeDataString($"e2e-{Guid.NewGuid():N}@localhost");
        await page.GotoAsync($"{BaseUrl}/dev-login?email={user}&returnUrl={Uri.EscapeDataString(path)}");
        return page;
    }
}
