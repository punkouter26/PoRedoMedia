using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Shared;

/// <summary>The on-device models, and what a run is expected to cost.</summary>
public static class AiCatalog
{
    /// <summary>
    /// Model ids that run on the user's device start with this. A device model is never sent to a
    /// cloud provider: the device does the work and the run carries its result.
    /// </summary>
    public const string BrowserPrefix = "browser:";

    /// <summary>The vision model that runs in the user's browser. Its weights download on first use.</summary>
    public const string BrowserVision = BrowserPrefix + "florence2-base";

    public static bool RunsInBrowser(string? modelId) => modelId?.StartsWith(BrowserPrefix, StringComparison.Ordinal) == true;

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
                // One call reads the picture and writes the bars; a refused song costs a second draft.
                MediaFunction.RapRoast => look + prices.TextUsd + prices.MusicUsd,
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
