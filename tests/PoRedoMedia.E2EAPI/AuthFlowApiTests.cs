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

    [Fact]
    public async Task Sign_out_is_a_write_that_needs_the_antiforgery_token()
    {
        var client = factory.CreateNoRedirectClient();
        await client.GetAsync("/dev-login?email=dev@example.com");

        // Another site can make a browser send this request, but not with the token.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/logout", null)).StatusCode);

        var token = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<System.Text.Json.JsonElement>(client, "/api/antiforgery/token");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/quota")).StatusCode);
    }
}
