using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Api.Common;

namespace PoRedoMedia.IntegrationTests;

public sealed class AntiforgeryTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        builder.Services.AddPoAntiforgery(builder.Environment);
        _app = builder.Build();
        _app.UseAntiforgery();
        _app.MapAntiforgeryToken();
        var group = _app.MapGroup("/things").RequireAntiforgeryValidation();
        group.MapGet("/", () => "read");
        group.MapPost("/", () => "written");
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task A_write_without_a_token_is_rejected_with_400()
    {
        var response = await _client.PostAsync("/things/", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_read_needs_no_token()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/things/")).StatusCode);
    }

    [Fact]
    public async Task A_write_with_the_cookie_and_header_pair_is_accepted()
    {
        var tokenResponse = await _client.GetAsync("/api/antiforgery/token");
        var token = (await tokenResponse.Content.ReadFromJsonAsync<AntiforgeryTokenDto>())!.Token;
        var cookie = tokenResponse.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

        var request = new HttpRequestMessage(HttpMethod.Post, "/things/");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add(AntiforgeryExtensions.TokenHeaderName, token);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
