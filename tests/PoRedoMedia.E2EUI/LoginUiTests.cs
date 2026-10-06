using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class LoginUiTests : UiTestBase
{
    [LiveServerFact]
    public async Task A_signed_out_visitor_is_sent_to_login_and_can_sign_in_and_out()
    {
        var page = await Browser.NewPageAsync();

        await page.GotoAsync(BaseUrl + "/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await page.GetByRole(AriaRole.Button, new() { Name = "Developer sign-in" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Create", Exact = true })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByText("dev@localhost")).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }

    /// <summary>
    /// Microsoft sign-in comes back from another site and is then redirected into the app. This
    /// arrives the same way, from another site through the developer sign-in, without Microsoft.
    /// </summary>
    [LiveServerFact]
    public async Task A_sign_in_that_arrives_from_another_site_lands_signed_in()
    {
        var page = await Browser.NewPageAsync();
        var signIn = $"{BaseUrl}/dev-login?email={Uri.EscapeDataString($"e2e-{Guid.NewGuid():N}@localhost")}&returnUrl=%2Fgallery";
        await page.RouteAsync("http://identity.example/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = $"<a href=\"{System.Net.WebUtility.HtmlEncode(signIn)}\">Back to the app</a>",
        }));
        await page.GotoAsync("http://identity.example/");

        await page.GetByRole(AriaRole.Link, new() { Name = "Back to the app" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Gallery", Exact = true })).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }
}
