using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Common;

// Contracts a slice uses to reach behaviour another slice owns.

/// <summary>
/// One function, or a group of functions that must run together. Image functions are one step
/// each; the video functions are one step, because they end in a single render.
/// </summary>
public interface IRunStep
{
    /// <summary>The functions this step performs when they are in the run.</summary>
    IReadOnlySet<MediaFunction> Handles { get; }

    /// <summary>
    /// Does the work and reports each result through <see cref="RunContext.AddOutputAsync"/>.
    /// Throw <see cref="RunStepException"/> for a failure the user should read.
    /// </summary>
    Task ExecuteAsync(RunContext context, CancellationToken ct);
}

/// <summary>A step failure whose message is written for the user.</summary>
public sealed class RunStepException(string userMessage, Exception? inner = null) : Exception(userMessage, inner);

/// <summary>What a step sees of the run it is part of.</summary>
public sealed class RunContext(Run run, MediaItem source, Func<MediaItem, Task> onOutput, Func<string, Task> onMessage)
{
    private readonly List<string> _notes = [];

    public Run Run { get; } = run;

    /// <summary>The media the user picked.</summary>
    public MediaItem Source { get; } = source;

    /// <summary>
    /// The image the next step should work on: the source, until a step replaces it. Restyle then
    /// Meme caption each replace it; Rap roast and Photo to video only read it.
    /// </summary>
    public MediaItem Current { get; private set; } = source;

    public IReadOnlyList<string> Notes => _notes;

    public bool Has(MediaFunction function) => Run.Functions.Contains(function);

    public string? Option(string key) => Run.Options.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>Records a result. With <paramref name="becomesCurrent"/> later steps work on it.</summary>
    public async Task AddOutputAsync(MediaItem output, bool becomesCurrent = false)
    {
        if (becomesCurrent)
            Current = output;
        await onOutput(output);
    }

    /// <summary>A line for the progress display, such as "Writing the caption".</summary>
    public Task ReportAsync(string message) => onMessage(message);

    /// <summary>Something the user should know that did not fail the step, such as a fallback that was used.</summary>
    public void AddNote(string note) => _notes.Add(note);
}

public interface IRunNotifier
{
    Task ProgressAsync(RunProgressDto progress, CancellationToken ct = default);
}

public interface IRenderQuota
{
    Task<QuotaStatusDto> GetStatusAsync(UserId userId, CancellationToken ct = default);

    Task<(bool Allowed, QuotaStatusDto Status)> TryConsumeAsync(UserId userId, CancellationToken ct = default);

    Task RefundAsync(UserId userId, CancellationToken ct = default);
}
