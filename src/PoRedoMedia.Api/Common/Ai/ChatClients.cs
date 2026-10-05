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

/// <summary>
/// The call shape the ported roast code uses, over an <see cref="IChatClient"/>. With no client it
/// reports itself unconfigured, which sends callers down their own stated fallbacks.
/// </summary>
public sealed class ChatCompletions(IChatClient? chat = null) : IChatCompletionService
{
    public bool IsConfigured => chat is not null;

    public async Task<ChatCompletionResult> CompleteAsync(
        string systemPrompt, string userPrompt, byte[]? image = null, string? jsonSchema = null, CancellationToken ct = default)
    {
        if (chat is null)
            throw new InvalidOperationException("No chat model is configured.");

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        List<AIContent> content = image is null ? [] : [new DataContent(image, "image/jpeg")];
        content.Add(new TextContent(userPrompt));
        var options = jsonSchema is null
            ? null
            : new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(System.Text.Json.JsonDocument.Parse(jsonSchema).RootElement, "answer") };

        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.System, systemPrompt), new ChatMessage(ChatRole.User, content)], options, ct);
        return new ChatCompletionResult(
            response.Text, (int)(response.Usage?.TotalTokenCount ?? 0), (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}

