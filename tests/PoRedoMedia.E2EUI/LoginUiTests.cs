using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class LoginUiTests : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable("HEADED") != "1" });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [LiveServerFact]
    public async Task A_signed_out_visitor_is_sent_to_login_and_can_sign_in_and_out()
    {
        var page = await _browser.NewPageAsync();

        await page.GotoAsync(LiveServerFactAttribute.BaseUrl + "/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await page.GetByRole(AriaRole.Button, new() { Name = "Developer sign-in" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Create" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByText("dev@localhost")).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }
}
