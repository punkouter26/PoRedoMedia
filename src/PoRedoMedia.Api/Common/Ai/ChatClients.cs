using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>Builds the image-side chat clients behind one <see cref="IChatClient"/> abstraction.</summary>
public static class ChatClients
{
    public static IChatClient AzureOpenAi(Uri endpoint, string key, string deployment, AzureOpenAIClientOptions? options = null) =>
        new AzureOpenAIClient(endpoint, new AzureKeyCredential(key), options ?? new AzureOpenAIClientOptions()).GetChatClient(deployment).AsIChatClient();
}
