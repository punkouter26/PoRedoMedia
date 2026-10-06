using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace PoRedoMedia.Api.Features.Auth;

public static class AuthServiceExtensions
{
    /// <summary>
    /// Registers authentication and deny-by-default authorization. Three modes, chosen at startup:
    /// header-driven fake auth (tests, never Production), cookie only when no Entra client id is
    /// configured, and Microsoft Entra OIDC behind the cookie otherwise.
    /// </summary>
    public static IServiceCollection AddPoRedoMediaAuth(
        this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        // Fail closed: an endpoint is authenticated unless it says .AllowAnonymous().
        services.AddAuthorization(options => options.FallbackPolicy =
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddCascadingAuthenticationState();

        if (configuration.GetValue<bool>(ConfigKeys.AuthEnableFakeAuth) && environment.IsDevOrTest())
        {
            services.AddAuthentication(FakeAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, _ => { });
            return services;
        }

        var clientId = configuration[ConfigKeys.AzureAdClientId];
        var hasOidc = !string.IsNullOrWhiteSpace(clientId);

        // Production must never silently degrade to cookie-only: that drops the identity provider.
        if (!hasOidc && environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"{ConfigKeys.AzureAdClientId} is required in Production. It resolves from the " +
                "PoRedoMedia--AzureAd--ClientId Key Vault secret.");
        }

        // The cookie is the default challenge too, so a signed-out page hit lands on /login rather
        // than jumping straight to Microsoft. /challenge-microsoft invokes OIDC explicitly.
        var auth = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/login";
                options.Cookie.HttpOnly = true;
                // Lax, not Strict: Microsoft sends the browser back from its own site, and a Strict
                // cookie set on that return is withheld from the redirect into the app, so the
                // user arrived signed out. Writes are still protected by the antiforgery token.
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Dev and Test run over plain HTTP, where a Secure cookie is silently discarded.
                options.Cookie.SecurePolicy = environment.IsDevOrTest()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                // API callers get a 401, not a redirect to a login page they cannot render.
                options.Events.OnRedirectToLogin = ctx =>
                {
                    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs"))
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    else
                        ctx.Response.Redirect(ctx.RedirectUri);
                    return Task.CompletedTask;
                };
            });

        if (!hasOidc)
            return services;

        var tenantId = configuration[ConfigKeys.AzureAdTenantId] ?? "common";
        auth.AddOpenIdConnect(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
            options.ClientId = clientId;
            options.ClientSecret = configuration[ConfigKeys.AzureAdClientSecret];
            options.ResponseType = "code";
            // The browser never holds a token; only the claims-only cookie leaves the server.
            options.SaveTokens = false;
            options.CallbackPath = configuration[ConfigKeys.AzureAdCallbackPath] ?? "/signin-oidc";
            options.SignedOutCallbackPath = configuration[ConfigKeys.AzureAdSignedOutCallbackPath] ?? "/signout-oidc";
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.GetClaimsFromUserInfoEndpoint = true;
            options.TokenValidationParameters.NameClaimType = "name";

            // Without a list, any Microsoft account can sign in and spend the daily AI allowance.
            var allowedEmails = AllowedEmails(configuration);
            options.Events.OnTicketReceived = context =>
            {
                if (!IsAllowed(allowedEmails, context.Principal))
                    context.Fail("This account is not on the allowed list.");
                return Task.CompletedTask;
            };
            // A refused or abandoned sign-in lands on the login page with a reason, not an error page.
            options.Events.OnRemoteFailure = context =>
            {
                context.Response.Redirect("/login?denied=true");
                context.HandleResponse();
                return Task.CompletedTask;
            };

            // Restrict to listed tenants when configured; otherwise accept any well-formed Entra
            // v2 issuer (multi-tenant and personal accounts) and nothing else.
            var allowedTenants = configuration[ConfigKeys.AzureAdAllowedTenantIds]?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (allowedTenants?.Length > 0)
            {
                options.TokenValidationParameters.ValidIssuers =
                    allowedTenants.Select(t => $"https://login.microsoftonline.com/{t}/v2.0").ToArray();
            }
            else
            {
                options.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
                    issuer is { } i
                    && i.StartsWith("https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase)
                    && i.EndsWith("/v2.0", StringComparison.OrdinalIgnoreCase)
                        ? i
                        : throw new SecurityTokenInvalidIssuerException($"Untrusted issuer: {issuer}");
            }
        });

        return services;
    }

    internal static string[] AllowedEmails(IConfiguration configuration) =>
        configuration[ConfigKeys.AuthAllowedEmails]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    /// <summary>True when no list is configured, or one of the account's addresses is on it.</summary>
    internal static bool IsAllowed(IReadOnlyCollection<string> allowedEmails, ClaimsPrincipal? user) =>
        allowedEmails.Count == 0
        || (user?.Claims ?? [])
            .Where(c => c.Type is ClaimTypes.Email or "email" or "preferred_username")
            .Any(c => allowedEmails.Contains(c.Value, StringComparer.OrdinalIgnoreCase));
}
