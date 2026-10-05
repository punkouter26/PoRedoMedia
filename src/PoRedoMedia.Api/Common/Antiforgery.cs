using Microsoft.AspNetCore.Antiforgery;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// Antiforgery for a cookie-authenticated JSON API.
/// </summary>
/// <remarks>
/// <c>app.UseAntiforgery()</c> alone protects nothing here: the middleware validates only form
/// posts, so a JSON write passes with no token. Enforcement is the endpoint filter below. Apply it
/// at the group level, so a write added to an existing group is protected by default. Do not also
/// set <c>RequiresValidation = true</c> on the endpoint: the middleware then marks the request
/// unvalidated and the filter rejects even a correct token.
/// </remarks>
public static class AntiforgeryExtensions
{
    /// <summary>Header the client sends the request token in.</summary>
    public const string TokenHeaderName = "X-CSRF-TOKEN";

    public static IServiceCollection AddPoAntiforgery(this IServiceCollection services, IWebHostEnvironment environment) =>
        services.AddAntiforgery(options =>
        {
            options.HeaderName = TokenHeaderName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // Unlike the auth cookie, antiforgery THROWS when asked for a Secure cookie over HTTP,
            // which Dev and Test use.
            options.Cookie.SecurePolicy = environment.IsDevOrTest()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
        });

    /// <summary>Requires a valid token on every POST, PUT, PATCH and DELETE in the group.</summary>
    public static RouteGroupBuilder RequireAntiforgeryValidation(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter<AntiforgeryValidationFilter>();
        return group;
    }

    /// <summary>
    /// Issues the request half of the token pair. The secret half goes in an HttpOnly cookie the
    /// browser cannot read, so the client needs this route to get the half it must echo back.
    /// Anonymous because the client primes its token before sign-in.
    /// </summary>
    public static IEndpointRouteBuilder MapAntiforgeryToken(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/antiforgery/token", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            return TypedResults.Ok(new AntiforgeryTokenDto(tokens.RequestToken ?? string.Empty));
        }).AllowAnonymous();
        return app;
    }
}

public sealed record AntiforgeryTokenDto(string Token);

internal sealed class AntiforgeryValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var method = http.Request.Method;
        var isWrite = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
        if (!isWrite)
            return await next(context);

        try
        {
            await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem(
                title: "Invalid antiforgery token",
                detail: $"Fetch a token from /api/antiforgery/token and resend it in the {AntiforgeryExtensions.TokenHeaderName} header.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }
}
