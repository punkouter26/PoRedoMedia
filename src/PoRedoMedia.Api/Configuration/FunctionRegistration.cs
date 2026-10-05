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

        if (MockAi.IsEnabled(configuration, environment))
        {
            services.AddSingleton<MockVisionService>();
            services.AddSingleton<IVisionServiceRouter>(s => s.GetRequiredService<MockVisionService>());
            services.AddSingleton<ICaptionWriter, MockCaptionWriter>();
            services.AddSingleton<IRunStep, MemeCaptionStep>();
        }

        return services;
    }
}
