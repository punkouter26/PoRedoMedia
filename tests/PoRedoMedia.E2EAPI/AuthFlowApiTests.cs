using System.Net;

namespace PoRedoMedia.E2EAPI;

public sealed class AuthFlowApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Dev_login_issues_an_HttpOnly_strict_cookie_and_redirects_home()
    {
        var response = await factory.CreateNoRedirectClient().GetAsync("/dev-login?email=dev@example.com");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    public async Task Dev_login_refuses_an_off_site_return_url(string returnUrl)
    {
        var response = await factory.CreateNoRedirectClient()
            .GetAsync($"/dev-login?email=dev@example.com&returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }
}
