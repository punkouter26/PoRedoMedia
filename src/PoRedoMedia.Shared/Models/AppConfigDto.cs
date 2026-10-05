using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Shared.Models;

/// <summary>What the client needs to know about how the server is configured.</summary>
/// <param name="AvailableFunctions">Functions that can run here. The rest are shown as unavailable.</param>
/// <param name="RoastVoices">Voices the video roast can be performed in here, in menu order.</param>
/// <param name="VisionModels">Ids of the vision providers configured here, most preferred first.</param>
/// <param name="Pricing">List prices used for the session cost estimate.</param>
public sealed record AppConfigDto(
    bool UseMockAi, bool DevLoginEnabled, MediaFunction[] AvailableFunctions, string[] RoastVoices, string[] VisionModels, AiPricingDto Pricing);

/// <summary>Indicative list prices in US dollars per call. Estimates, not billed amounts.</summary>
public sealed record AiPricingDto(decimal VisionUsd, decimal TextUsd, decimal ImageUsd, decimal MusicUsd, decimal VideoUsd);
