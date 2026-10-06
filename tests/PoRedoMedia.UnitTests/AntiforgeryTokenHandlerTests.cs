using System.Net;
using PoRedoMedia.Client.Http;

namespace PoRedoMedia.UnitTests;

public sealed class AntiforgeryTokenHandlerTests
{
    [Fact]
    public async Task A_rejected_write_is_replayed_once_with_a_fresh_token_and_the_same_body()
    {
        var api = new FakeApi();
        var handler = new AntiforgeryTokenHandler(() => new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://app/") })
        {
            InnerHandler = api,
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://app/") };

        var response = await client.PostAsync("api/things", new StringContent("payload"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["token-1", "token-2"], api.TokensSeenOnWrites);
        Assert.Equal(["payload", "payload"], api.BodiesSeenOnWrites);
    }

    [Fact]
    public async Task A_400_that_is_the_endpoints_own_answer_is_not_sent_again()
    {
        var api = new FakeApi { Refusal = """{"detail":"Videos can be up to 1 minute long."}""" };
        var handler = new AntiforgeryTokenHandler(() => new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://app/") })
        {
            InnerHandler = api,
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://app/") };

        var response = await client.PostAsync("api/things", new StringContent("payload"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(api.TokensSeenOnWrites);
        Assert.Contains("1 minute", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_read_is_sent_without_fetching_a_token()
    {
        var api = new FakeApi();
        using var client = new HttpClient(new AntiforgeryTokenHandler(() => throw new InvalidOperationException()) { InnerHandler = api })
        {
            BaseAddress = new Uri("http://app/"),
        };

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/things")).StatusCode);
    }

    /// <summary>Issues token-1, token-2, ... and accepts a write only from token-2 onwards.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        private int _issued;
        public List<string> TokensSeenOnWrites { get; } = [];
        public List<string> BodiesSeenOnWrites { get; } = [];

        /// <summary>What a refused write answers with. The default is the server's antiforgery refusal.</summary>
        public string Refusal { get; init; } = """{"title":"Invalid antiforgery token"}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/antiforgery/token")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"token":"token-{{++_issued}}"}""") };
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK);

            var token = request.Headers.GetValues("X-CSRF-TOKEN").Single();
            TokensSeenOnWrites.Add(token);
            BodiesSeenOnWrites.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return token == "token-1"
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(Refusal) }
                : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
