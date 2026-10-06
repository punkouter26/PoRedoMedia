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

    /// <summary>Things the user should know that did not fail the run, such as a fallback that was used.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    public RunDto ToDto() => new(
        Id.Value, SourceId.Value, [.. Functions], Status, CurrentStep, Error,
        [.. OutputIds.Select(o => o.Value)], [.. Notes], CreatedAt, new(Options));
}
