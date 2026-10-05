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
}
