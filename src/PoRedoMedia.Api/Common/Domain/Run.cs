using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Common;

/// <summary>One run: a set of functions applied to one source, executed as a single job.</summary>
public sealed record Run
{
    public required UserId Owner { get; init; }
    public required RunId Id { get; init; }
    public required MediaId SourceId { get; init; }
    public required IReadOnlyList<MediaFunction> Functions { get; init; }
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
    public RunStatus Status { get; init; }
    public MediaFunction? CurrentStep { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<MediaId> OutputIds { get; init; } = [];

    /// <summary>The output later steps work on, once a step has replaced the source. Lets a resumed run carry on from it.</summary>
    public MediaId? WorkingId { get; init; }

    /// <summary>How many times the run has been started: 1, plus one for each restart that cut it short.</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>Things the user should know that did not fail the run, such as a fallback that was used.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// The progress lines the run has reported, oldest first: what the AI found and decided. Kept
    /// with the run because a page that subscribes a moment late would otherwise never see them.
    /// </summary>
    public IReadOnlyList<string> Log { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The estimated AI spend of the run's last attempt, in USD; null until it ends.</summary>
    public decimal? AiCostUsd { get; init; }

    public RunDto ToDto() => new(
        Id.Value, SourceId.Value, [.. Functions], Status, CurrentStep, Error,
        [.. OutputIds.Select(o => o.Value)], [.. Notes], CreatedAt, new(Options), [.. Log]);
}
