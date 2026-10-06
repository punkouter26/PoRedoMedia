using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace PoRedoMedia.Api.Features.Auth;

/// <summary>Sign-in and sign-out actions. The login page itself lives in the Client at /login.</summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // Dev/Test-only sign-in, used by the login page's dev button and by the E2E suites.
        if (app.Environment.IsDevOrTest())
            app.MapGet("/dev-login", DevLoginAsync).AllowAnonymous();

        app.MapGet("/challenge-microsoft", ChallengeMicrosoftAsync).AllowAnonymous();
        // A POST with the antiforgery token: as a GET, any other site could sign a user out.
        app.MapPost("/logout", SignOutAsync).AddEndpointFilter<AntiforgeryValidationFilter>();
    }

    private static async Task DevLoginAsync(HttpContext context, string? email, string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            context.Response.Redirect("/login");
            return;
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, $"dev|{email}"),
            new(ClaimTypes.Name, email),
            new(ClaimTypes.Email, email),
        ];
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        context.Response.Redirect(LocalOrHome(returnUrl));
    }

    private static async Task ChallengeMicrosoftAsync(
        HttpContext context, IWebHostEnvironment env, IConfiguration configuration, string? returnUrl)
    {
        var destination = LocalOrHome(returnUrl);

        if (string.IsNullOrWhiteSpace(configuration[ConfigKeys.AzureAdClientId]))
        {
            // No Entra registration configured: Dev/Test simulate the sign-in, anything else has
            // nothing to challenge with.
            context.Response.Redirect(env.IsDevOrTest()
                ? $"/dev-login?email=developer%40microsoft.local&returnUrl={Uri.EscapeDataString(destination)}"
                : "/login");
            return;
        }

        await context.ChallengeAsync(
            OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = destination });
    }

    /// <summary>Ends this app's session. The Microsoft session is left alone; the page goes to /login itself.</summary>
    private static async Task<IResult> SignOutAsync(HttpContext context, IAuthenticationSchemeProvider schemes)
    {
        // Header-driven fake auth (tests) has no cookie to clear.
        if (await schemes.GetSchemeAsync(CookieAuthenticationDefaults.AuthenticationScheme) is not null)
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    /// <summary>Open-redirect guard: only a site-relative path is honoured.</summary>
    private static string LocalOrHome(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
        // Browsers drop tabs and newlines from a URL, which would turn "/	/host" into "//host".
        && !returnUrl.Any(char.IsControl)
            ? returnUrl
            : "/";
}
