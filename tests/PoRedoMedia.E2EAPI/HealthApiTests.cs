using System.Net;

namespace PoRedoMedia.E2EAPI;

public sealed class HealthApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Live_probe_answers_200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
