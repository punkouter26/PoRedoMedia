using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Shared;

/// <summary>One choice in the model picker.</summary>
public sealed record AiModelOption(string Id, string Name, string Hint);

/// <summary>What the model picker offers, and what a run is expected to cost.</summary>
public static class AiCatalog
{
    /// <summary>The vision model that runs in the user's browser. Its weights download on first use.</summary>
    public const string BrowserVision = AiProviderIds.BrowserPrefix + "florence2-base";

    private static readonly AiModelOption[] Vision =
    [
        new(AiProviderIds.GeminiVision, "Google Gemini", "Real sentences, about 2 seconds"),
        new(AiProviderIds.AzureOpenAiVision, "Azure OpenAI", "The most detailed descriptions"),
        new(AiProviderIds.AzureComputerVision, "Azure Computer Vision", "Tags only in this region"),
        new(AiProviderIds.OllamaVision, "Ollama", "A model on this machine (development)"),
        new(BrowserVision, "On this device (Florence-2)", "About 230 MB the first time, then free"),
    ];

    /// <summary>
    /// The choices for looking at a picture: automatic, then what the server has configured in its
    /// order of preference, then the on-device model.
    /// </summary>
    public static IReadOnlyList<AiModelOption> VisionOptions(IEnumerable<string> serverModels) =>
        [Automatic, .. serverModels.Append(BrowserVision).Select(id => Vision.FirstOrDefault(o => o.Id == id)).OfType<AiModelOption>()];

    /// <summary>No choice made: the server uses the best model it has configured.</summary>
    public static readonly AiModelOption Automatic = new("", "Automatic", "The server uses its best configured model");

    public static bool RunsInBrowser(string? modelId) => modelId?.StartsWith(AiProviderIds.BrowserPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// An estimate, from list prices, of what one run costs. It is shown to the user as a guide
    /// and is not what was billed.
    /// </summary>
    public static decimal EstimateCost(IReadOnlyCollection<MediaFunction> functions, IReadOnlyDictionary<string, string> options, AiPricingDto prices)
    {
        var local = RunsInBrowser(options.GetValueOrDefault(RunOptions.VisionModel));
        var look = local ? 0 : prices.VisionUsd;
        var total = 0m;
        foreach (var function in functions)
        {
            total += function switch
            {
                MediaFunction.Restyle => (local ? 0 : prices.TextUsd) + prices.ImageUsd,
                MediaFunction.MemeCaption => options.GetValueOrDefault(RunOptions.MemeMode, RunOptions.MemeModeAi) == RunOptions.MemeModeAi ? look + prices.TextUsd : 0,
                MediaFunction.RapRoast => look + 2 * prices.TextUsd + prices.MusicUsd,
                MediaFunction.PhotoToVideo => prices.VideoUsd,
                MediaFunction.BulkStyles => prices.ImageUsd * Math.Max(1, options.Keys.Count(k => k.StartsWith("BulkStyles.prompt", StringComparison.Ordinal))),
                MediaFunction.Memeify => prices.VisionUsd + prices.TextUsd,
                MediaFunction.VideoRoast => 2 * prices.TextUsd,
                _ => 0,
            };
        }

        return total;
    }
}
