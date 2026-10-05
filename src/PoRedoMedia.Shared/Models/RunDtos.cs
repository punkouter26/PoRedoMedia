using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Shared.Models;

public enum RunStatus
{
    Queued,
    Running,
    Complete,

    /// <summary>A step failed. Outputs made before it are kept; <c>Error</c> says what went wrong.</summary>
    Failed,
}

/// <summary>Apply these functions to this source. Options are keyed <c>function.option</c>.</summary>
public sealed record RunRequest(Guid SourceId, MediaFunction[] Functions, Dictionary<string, string>? Options = null);

public sealed record RunDto(
    Guid Id,
    Guid SourceId,
    MediaFunction[] Functions,
    RunStatus Status,
    MediaFunction? CurrentStep,
    string? Error,
    Guid[] OutputIds,
    string[] Notes,
    DateTimeOffset CreatedAt);

/// <summary>One progress event for a run, pushed over the hub.</summary>
public sealed record RunProgressDto(
    Guid RunId,
    RunStatus Status,
    MediaFunction? Step,
    int Percent,
    string Message,
    MediaDto? Output = null);
