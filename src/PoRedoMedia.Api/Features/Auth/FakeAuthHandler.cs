using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace PoRedoMedia.Api.Features.Auth;

/// <summary>
/// Header-driven fake authentication for the BFF "local bypass" pipeline, enabled by
/// <c>Auth:EnableFakeAuth=true</c> (never in Production). A request authenticates itself by
/// sending <c>X-Fake-User</c> (the user id / name) and optional <c>X-Fake-Roles</c>
/// (comma-separated). This lets headless Playwright "golden path" tests assume any identity or
/// role set without driving the interactive Microsoft / GUEST login flow.
///
/// Absent <c>X-Fake-User</c> → <see cref="AuthenticateResult.NoResult"/> so anonymous endpoints
/// still serve and protected ones return 401, exactly like the real pipeline.
/// </summary>
public sealed class FakeAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "FakeAuth";
    public const string UserHeader = "X-Fake-User";
    public const string RolesHeader = "X-Fake-Roles";

    public FakeAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IWebHostEnvironment env) : base(options, logger, encoder)
    {
        // Defence in depth: this scheme must never run in Production even if misconfigured.
        if (env.IsProduction())
        {
            throw new InvalidOperationException(
                "FakeAuthHandler must never be registered in Production — it would let any caller " +
                "authenticate as an arbitrary user/role via the X-Fake-User header.");
        }
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrWhiteSpace(user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var userId = user.ToString();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId),
        };

        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            foreach (var role in roles.ToString()
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
