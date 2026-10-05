using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Api.Features.BulkStyles;
using PoRedoMedia.Api.Features.MemeCaption;
using PoRedoMedia.Api.Features.PhotoToVideo;
using PoRedoMedia.Api.Features.RapRoast;
using PoRedoMedia.Api.Features.Restyle;

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
        services.AddSingleton<BulkPromptRepository>();
        services.AddHybridCache();
        // Google calls can be slow and occasionally fail; retry them, but never retry a timeout.
        services.AddHttpClient(GeminiVisionService.HttpClientName).AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(4);
            o.Retry.MaxRetryAttempts = 2;
        });

        // A Veo call can be slow, and retrying a job start would bill twice: long timeout, no retries.
        services.AddHttpClient(VeoVideoService.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(15));

        if (MockAi.IsEnabled(configuration, environment))
        {
            services.AddSingleton<MockVisionService>();
            services.AddSingleton<IVisionServiceRouter>(s => s.GetRequiredService<MockVisionService>());
            services.AddSingleton<ICaptionWriter, MockCaptionWriter>();
            services.AddSingleton<IRunStep, MemeCaptionStep>();
            services.AddSingleton<IChatClient, MockChatClient>();
            services.AddSingleton<IImageGenerationService, MockImageGenerationService>();
            services.AddSingleton<ReproductionPromptWriter>();
            services.AddSingleton<IRunStep, RestyleStep>();
            services.AddSingleton<IRunStep, BulkStylesStep>();
            // No chat model in mock mode: the roast code then takes its own built-in fallbacks.
            services.AddSingleton<IChatCompletionService>(new ChatCompletions());
            services.AddSingleton<ISceneDetailProvider, NullSceneDetailProvider>();
            services.AddSingleton<IMusicGenerationService, MockMusicService>();
            AddRoast(services);
            services.AddSingleton<IVideoGenerationService, MockVideoService>();
            services.AddSingleton<IRunStep, PhotoToVideoStep>();
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

            if (Has(ConfigKeys.GoogleApiKey))
            {
                services.AddSingleton<IImageGenerationService, GeminiImageService>();
                services.AddSingleton<ReproductionPromptWriter>();
                services.AddSingleton<IRunStep, RestyleStep>();
            }
        }

        if (Has(ConfigKeys.GoogleApiKey))
        {
            services.TryAddSingleton<IImageGenerationService, GeminiImageService>();
            services.AddSingleton<IRunStep, BulkStylesStep>();
            services.AddSingleton<IVideoGenerationService, VeoVideoService>();
            services.AddSingleton<IRunStep, PhotoToVideoStep>();

            if (canSeeImages)
            {
                // The roast needs a performer (Lyria, on the Google key) and something that can see.
                // Without a chat model it still runs, on its built-in lyrics, and says so.
                services.AddSingleton<IChatCompletionService>(new ChatCompletions(chat));
                services.AddSingleton<ISceneDetailProvider, AzureSceneDetailService>();
                services.AddSingleton<IMusicGenerationService, LyriaMusicService>();
                AddRoast(services);
            }
        }

        return services;

        bool Has(string key) => !string.IsNullOrWhiteSpace(configuration[key]);
    }

    private static void AddRoast(IServiceCollection services)
    {
        services.AddSingleton<SceneDescriber>();
        services.AddSingleton<RoastLyricsWriter>();
        services.AddSingleton<IRunStep, RapRoastStep>();
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
