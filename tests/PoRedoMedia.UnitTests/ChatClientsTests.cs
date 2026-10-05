using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using OpenAI;
using Microsoft.Extensions.AI;
using PoRedoMedia.Api.Common.Ai;

namespace PoRedoMedia.UnitTests;

public sealed class ChatClientsTests
{
    private const string CannedCompletion = """
        {"id":"x","object":"chat.completion","created":0,"model":"gpt-5.4-nano",
         "choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """;

    // Azure is reached through the plain OpenAI SDK on its v1 endpoint. The request must actually
    // travel the pipeline and land on that path, not merely compile.
    [Fact]
    public async Task Azure_OpenAI_chat_round_trips_through_IChatClient()
    {
        var handler = new StubHandler();
        var options = new OpenAIClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(handler)) };
        using IChatClient client = ChatClients.AzureOpenAi(new Uri("https://example.openai.azure.com/"), "not-a-real-key", "gpt-5.4-nano", options);

        var response = await client.GetResponseAsync("hi");

        Assert.Equal("hello", response.Text);
        Assert.Contains("/openai/v1/chat/completions", handler.LastPath);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedCompletion, Encoding.UTF8, "application/json"),
            });
        }
    }
}
