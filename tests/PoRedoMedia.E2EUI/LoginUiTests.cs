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
}
