using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Api.Features.BulkStyles;
using PoRedoMedia.Api.Features.Video;
using PoRedoMedia.Api.Features.MemeCaption;
using PoRedoMedia.Api.Features.PhotoToVideo;
using PoRedoMedia.Api.Features.RapRoast;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Api.Features.Sounds;
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
        services.AddSingleton<AiMeter>();
        services.AddSingleton<AiReadStore>();
        services.AddTransient<AiMeterHandler>();
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
        }).Services.AddHttpClient(GeminiVisionService.HttpClientName).AddHttpMessageHandler<AiMeterHandler>();

        // A Veo call can be slow, and retrying a job start would bill twice: long timeout, no retries.
        services.AddHttpClient(VeoVideoService.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(15)).AddHttpMessageHandler<AiMeterHandler>();

        AddVideo(services, configuration, environment);

        if (MockAi.IsEnabled(configuration, environment))
        {
            services.AddSingleton<IVisionService, MockVisionService>();
            services.AddSingleton<ICaptionWriter, MockCaptionWriter>();
            services.AddSingleton<IRunStep, MemeCaptionStep>();
            services.AddSingleton<IChatClient, MockChatClient>();
            services.AddSingleton<IImageGenerationService, MockImageGenerationService>();
            services.AddSingleton<ReproductionPromptWriter>();
            services.AddSingleton<IRunStep, RestyleStep>();
            services.AddSingleton<IRunStep, BulkStylesStep>();
            // No chat model in mock mode: the roast code then takes its own built-in fallbacks.
            services.AddSingleton<IChatCompletionService>(new ChatCompletions());
            services.AddSingleton<IMusicGenerationService, MockMusicService>();
            AddRoast(services);
            services.AddSingleton<IVideoGenerationService, MockVideoService>();
            services.AddSingleton<IRunStep, PhotoToVideoStep>();
            return services;
        }

        // Free-form text: Azure OpenAI.
        var chat = ChatClients.AzureOpenAi(configuration);

        // Looking at pictures: Gemini gives real sentences cheaply; Azure OpenAI is the fallback.
        var canSeeImages = Has(ConfigKeys.GoogleApiKey) || chat is not null;
        if (canSeeImages)
            services.AddSingleton<IVisionService>(s => Vision(s, configuration));

        if (chat is not null)
        {
            services.AddSingleton(s => Instrument(chat, s, "chat"));
            services.AddSingleton<AiJudge>();
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
                services.AddSingleton<IChatCompletionService>(s => new ChatCompletions(chat is null ? null : s.GetRequiredService<IChatClient>()));
                services.AddSingleton<IMusicGenerationService, LyriaMusicService>();
                AddRoast(services);
            }
        }

        return services;

        bool Has(string key) => !string.IsNullOrWhiteSpace(configuration[key]);
    }

    /// <summary>
    /// The video functions: the sound library and the render are always here; the AI parts are
    /// the mocks, or the Foundry services when Foundry is configured, or absent.
    /// </summary>
    private static void AddVideo(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<ISoundAssetRepository, SoundAssetTableRepository>();
        services.AddSingleton<ISemanticMatchingService, SemanticMatchingService>();
        services.AddSingleton<AiFoundryClient>();
        services.AddSingleton<FFmpegRenderService>();
        services.AddSingleton<IMediaToolkit>(s => s.GetRequiredService<FFmpegRenderService>());
        services.AddSingleton<SourceAudioAnalysis>();
        services.AddSingleton<ISourceAudioAnalysis>(s => s.GetRequiredService<SourceAudioAnalysis>());

        if (MockAi.IsEnabled(configuration, environment))
        {
            services.AddSingleton<IAiVisionService, MockFrameVision>();
            services.AddSingleton<IDirectorService, MockDirector>();
            services.AddSingleton<ITranscriptionService, MockTranscription>();
            services.AddSingleton<IVideoRoast, MockVideoRoast>();
        }
        else
        {
            // Registered either way: it reports itself disabled when no speech deployment is set.
            services.AddSingleton<ITranscriptionService, AiFoundryTranscriptionService>();
            var google = !string.IsNullOrWhiteSpace(configuration[ConfigKeys.GoogleApiKey]);
            var foundry = AiFoundryClient.Setting(configuration, "AiFoundry:Endpoint") is not null;

            // Gemini watches the whole clip, sound and all; Foundry's sampled frames are the fallback.
            if (google)
                services.AddSingleton<IClipVision, GeminiClipVisionService>();

            if (foundry)
            {
                services.AddSingleton<IAiVisionService, AiFoundryVisionService>();
                services.AddSingleton<IDirectorService, AiFoundryDirectorService>();
                services.AddSingleton<IVideoRoast, RoastService>();
            }
        }

        services.AddSingleton<IRunStep, VideoStep>();
    }

    private static void AddRoast(IServiceCollection services)
    {
        services.AddSingleton<SceneDescriber>();
        services.AddSingleton<RoastLyricsWriter>();
        services.AddSingleton<IRunStep, RapRoastStep>();
    }

    /// <summary>The one vision provider, with its answers cached by picture.</summary>
    private static CachingVisionService Vision(IServiceProvider services, IConfiguration configuration)
    {
        var (scope, provider) = string.IsNullOrWhiteSpace(configuration[ConfigKeys.GoogleApiKey])
            ? ("azure-openai", (IVisionService)new ChatVisionService(Instrument(ChatClients.AzureOpenAi(configuration)!, services, "vision")))
            : ("gemini", new GeminiVisionService(configuration, services.GetRequiredService<IHttpClientFactory>()));
        return new CachingVisionService(provider, services.GetRequiredService<HybridCache>(), scope);
    }

    /// <summary>A chat client with OpenTelemetry spans (gen_ai.*) and usage metering around it.</summary>
    private static IChatClient Instrument(IChatClient chat, IServiceProvider services, string stage) =>
        chat.AsBuilder()
            .UseOpenTelemetry(services.GetService<ILoggerFactory>(), PoTelemetry.Name)
            .Use(inner => new MeteredChatClient(inner, services.GetRequiredService<AiMeter>(), stage))
            .Build(services);
}
