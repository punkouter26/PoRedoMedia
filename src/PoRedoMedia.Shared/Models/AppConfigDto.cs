using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Shared.Models;

/// <summary>What the client needs to know about how the server is configured.</summary>
/// <param name="AvailableFunctions">Functions that can run here. The rest are shown as unavailable.</param>
public sealed record AppConfigDto(bool UseMockAi, bool DevLoginEnabled, MediaFunction[] AvailableFunctions);
