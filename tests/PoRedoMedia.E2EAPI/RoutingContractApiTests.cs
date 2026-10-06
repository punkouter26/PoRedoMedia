using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace PoRedoMedia.E2EAPI;

public sealed class RoutingContractApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // Every route a signed-out caller may reach. A new public route fails this test until it is
    // added here on purpose. Static files and the Blazor host itself are public by design and
    // are identified by their metadata, not listed.
    private static readonly string[] AnonymousAllowList =
    [
        "/api/antiforgery/token",
        "/api/config",
        "/challenge-microsoft",
        "/dev-login",
        "/health/live",
        "/v/{token}",
        "/v/{token}/content",
        "/v/{token}/thumb",
    ];

    [Fact]
    public void Anonymous_surface_is_exactly_the_allow_list()
    {
        var anonymous = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Where(e => !IsHostOrStaticAsset(e))
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(AnonymousAllowList, anonymous);
    }

    [Fact]
    public void Every_other_endpoint_requires_a_signed_in_user()
    {
        var policy = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    private static bool IsHostOrStaticAsset(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("_framework", StringComparison.Ordinal)
        || endpoint.Metadata.Any(m => m.GetType().Name is "StaticAssetDescriptor" or "ComponentTypeMetadata" or "RootComponentMetadata");
}
