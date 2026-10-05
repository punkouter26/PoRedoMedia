using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>Builds the image-side chat clients behind one <see cref="IChatClient"/> abstraction.</summary>
public static class ChatClients
{
    public const string DefaultDeployment = "gpt-5.4-nano";

    public static IChatClient AzureOpenAi(Uri endpoint, string key, string deployment, AzureOpenAIClientOptions? options = null) =>
        new AzureOpenAIClient(endpoint, new AzureKeyCredential(key), options ?? new AzureOpenAIClientOptions())
            .GetChatClient(deployment).AsIChatClient();

    /// <summary>The configured Azure OpenAI chat client, or null when its endpoint or key is missing.</summary>
    public static IChatClient? AzureOpenAi(IConfiguration configuration) =>
        configuration[ConfigKeys.OpenAiEndpoint] is { Length: > 0 } endpoint && configuration[ConfigKeys.OpenAiKey] is { Length: > 0 } key
            ? AzureOpenAi(new Uri(endpoint), key, configuration[ConfigKeys.OpenAiChatDeployment] is { Length: > 0 } d ? d : DefaultDeployment)
            : null;

    /// <summary>A client for a model on the local Ollama daemon. Development only.</summary>
    public static IChatClient Ollama(IConfiguration configuration, string model) =>
        new OllamaSharp.OllamaApiClient(
            new Uri(configuration[ConfigKeys.OllamaEndpoint] is { Length: > 0 } endpoint ? endpoint : "http://localhost:11434"), model);
}
