namespace PoRedoMedia.Shared.Models;

/// <summary>What the client needs to know about how the server is configured.</summary>
public sealed record AppConfigDto(bool UseMockAi, bool DevLoginEnabled);
