using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Api.Features.MemeCaption;

namespace PoRedoMedia.Api.Configuration;

public static class FunctionRegistration
{
    /// <summary>
    /// Registers the functions that can run in this configuration. A function is registered only
    /// when everything it needs is present, so one that is not registered is simply not offered.
    /// </summary>
    public static IServiceCollection AddFunctions(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<MemeTemplateService>();
        services.AddHybridCache();
        // Google calls can be slow and occasionally fail; retry them, but never retry a timeout.
        services.AddHttpClient(GeminiVisionService.HttpClientName).AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(4);
            o.Retry.MaxRetryAttempts = 2;
        });

        if (MockAi.IsEnabled(configuration, environment))
        {
            services.AddSingleton<MockVisionService>();
            services.AddSingleton<IVisionServiceRouter>(s => s.GetRequiredService<MockVisionService>());
            services.AddSingleton<ICaptionWriter, MockCaptionWriter>();
            services.AddSingleton<IRunStep, MemeCaptionStep>();
            return services;
        }

        // Free-form text: Azure OpenAI, or a local Ollama model when a developer opts in.
        var chat = environment.IsDevelopment() && configuration[ConfigKeys.OllamaChatModel] is { Length: > 0 } ollamaChat
            ? ChatClients.Ollama(configuration, ollamaChat)
            : ChatClients.AzureOpenAi(configuration);

        services.AddSingleton<IVisionServiceRouter>(s => new VisionServiceRouter(VisionProviders(s, configuration, environment)));
        var canSeeImages = Has(ConfigKeys.GoogleApiKey) || ChatClients.AzureOpenAi(configuration) is not null
            || (Has(ConfigKeys.ComputerVisionEndpoint) && Has(ConfigKeys.ComputerVisionApiKey));

        if (chat is not null)
        {
            services.AddSingleton(chat);
            services.AddSingleton<ICaptionWriter, ChatCaptionWriter>();
            if (canSeeImages)
                services.AddSingleton<IRunStep, MemeCaptionStep>();
        }

        return services;

        bool Has(string key) => !string.IsNullOrWhiteSpace(configuration[key]);
    }

    private static Dictionary<string, IVisionService> VisionProviders(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment)
    {
        var cache = services.GetRequiredService<HybridCache>();
        var providers = new Dictionary<string, IVisionService>();
        void Add(string id, IVisionService provider) => providers[id] = new CachingVisionService(provider, cache, id);

        if (!string.IsNullOrWhiteSpace(configuration[ConfigKeys.GoogleApiKey]))
            Add(AiProviderIds.GeminiVision, new GeminiVisionService(configuration, services.GetRequiredService<IHttpClientFactory>()));
        if (ChatClients.AzureOpenAi(configuration) is { } azureChat)
            Add(AiProviderIds.AzureOpenAiVision, new ChatVisionService(azureChat));
        if (!string.IsNullOrWhiteSpace(configuration[ConfigKeys.ComputerVisionEndpoint]) && !string.IsNullOrWhiteSpace(configuration[ConfigKeys.ComputerVisionApiKey]))
            Add(AiProviderIds.AzureComputerVision, new AzureVisionService(configuration));
        if (environment.IsDevelopment() && configuration[ConfigKeys.OllamaVisionModel] is { Length: > 0 } ollamaVision)
            Add(AiProviderIds.OllamaVision, new ChatVisionService(ChatClients.Ollama(configuration, ollamaVision)));

        return providers;
    }
}
