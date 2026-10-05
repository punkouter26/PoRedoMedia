using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PoRedoMedia.E2EAPI;

public sealed class HealthApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Live_probe_answers_200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
