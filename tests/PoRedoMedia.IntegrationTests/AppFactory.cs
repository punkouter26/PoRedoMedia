using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PoRedoMedia.Api.Common;

namespace PoRedoMedia.IntegrationTests;

/// <summary>
/// The real app over a throwaway Azurite, with header-driven fake sign-in and mock AI.
/// </summary>
public sealed class AppFactory(string storageConnectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("Storage:ConnectionString", storageConnectionString);
        builder.UseSetting("Auth:EnableFakeAuth", "true");
        builder.UseSetting("Mocks:UseMockAi", "true");
    }

    /// <summary>A client signed in as <paramref name="user"/> that already carries an antiforgery token.</summary>
    public async Task<HttpClient> SignedInAsync(string user)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Fake-User", user);
        var token = await client.GetFromJsonAsync<AntiforgeryTokenDto>("/api/antiforgery/token");
        client.DefaultRequestHeaders.Add(AntiforgeryExtensions.TokenHeaderName, token!.Token);
        return client;
    }
}
