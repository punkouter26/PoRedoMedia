using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PoRedoMedia.Api.Features.Auth;

namespace PoRedoMedia.UnitTests;

public sealed class FakeAuthHandlerTests
{
    [Fact]
    public void Cannot_be_constructed_in_Production()
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        Assert.Throws<InvalidOperationException>(() => new FakeAuthHandler(
            Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>(), NullLoggerFactory.Instance, UrlEncoder.Default, env));
    }

    [Fact]
    public void Only_listed_addresses_may_sign_in_once_a_list_is_set()
    {
        static bool Allowed(string[] list, string email) => AuthServiceExtensions.IsAllowed(
            list, new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new("preferred_username", email)])));

        Assert.True(Allowed([], "anyone@example.com"));
        Assert.True(Allowed(["owner@example.com", "friend@example.com"], "FRIEND@example.com"));
        Assert.False(Allowed(["owner@example.com"], "stranger@example.com"));
    }
}
