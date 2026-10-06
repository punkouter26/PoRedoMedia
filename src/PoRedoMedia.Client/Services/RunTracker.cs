using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>One run as the page shows it: where it is, what it has made, how it ended.</summary>
public sealed class TrackedRun(RunDto run)
{
    public RunDto Run { get; internal set; } = run;
    public RunStatus Status { get; internal set; } = run.Status;
    public MediaFunction? Step { get; internal set; } = run.CurrentStep;
    public double Percent { get; internal set; } = run.Status == RunStatus.Complete ? 100 : 0;
    public string Message { get; internal set; } = run.Error ?? "Waiting to start";
    public List<MediaDto> Outputs { get; } = [];
    public IReadOnlyList<string> Notes { get; internal set; } = run.Notes;
    public bool Finished => Status is RunStatus.Complete or RunStatus.Failed;

    internal IAsyncDisposable? Subscription { get; set; }
}

/// <summary>
/// Follows the user's runs for as long as the app is open, whichever page is showing. A run
/// started on Create keeps reporting after the user walks off to the gallery, and the header tray
/// and the Create page read the same state.
/// </summary>
public sealed class RunTracker(RunApi runs, MediaApi media) : IAsyncDisposable
{
    /// <summary>How many past runs the tray lists.</summary>
    private const int Recent = 8;

    private readonly List<TrackedRun> _runs = [];
    private Task? _loaded;
    private bool _disposed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<TrackedRun> Runs => _runs;

    public int Active => _runs.Count(r => !r.Finished);

    /// <summary>Raised on any change to any run. Handlers switch to their own renderer.</summary>
    public event Action? Changed;

    /// <summary>Raised once per run, when it ends either way.</summary>
    public event Action<TrackedRun>? Finished;

    /// <summary>Reads the recent runs once and picks up any that are still going.</summary>
    public Task LoadAsync() => _loaded ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        foreach (var run in (await runs.ListAsync()).Take(Recent))
        {
            if (_runs.Any(r => r.Run.Id == run.Id))
                continue;

            var tracked = new TrackedRun(run);
            _runs.Add(tracked);
            if (!tracked.Finished)
                _ = FollowAsync(tracked);
        }

        _runs.Sort((a, b) => b.Run.CreatedAt.CompareTo(a.Run.CreatedAt));
        Changed?.Invoke();
    }

    /// <summary>Starts following a run that was just started.</summary>
    public TrackedRun Track(RunDto run)
    {
        if (_runs.FirstOrDefault(r => r.Run.Id == run.Id) is { } known)
            return known;

        var tracked = new TrackedRun(run);
        _runs.Insert(0, tracked);
        if (!tracked.Finished)
            _ = FollowAsync(tracked);
        Changed?.Invoke();
        return tracked;
    }

    /// <summary>The run with this id, with its results loaded, or null when it is not the user's.</summary>
    public async Task<TrackedRun?> FindAsync(Guid id)
    {
        await LoadAsync();
        var tracked = _runs.FirstOrDefault(r => r.Run.Id == id);
        if (tracked is null)
        {
            try
            {
                if (await runs.GetAsync(id) is { } run)
                    tracked = Track(run);
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        if (tracked is not null)
            await LoadOutputsAsync(tracked);
        return tracked;
    }

    private async Task FollowAsync(TrackedRun tracked)
    {
        try
        {
            tracked.Subscription = await runs.FollowAsync(tracked.Run.Id, p => OnProgressAsync(tracked, p), () => ResyncAsync(tracked));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // No live updates. The run is already started and paid for, so fall back to asking.
            _ = PollAsync(tracked);
        }

        // The run may have moved on, or finished, before the subscription was in place.
        await ResyncAsync(tracked);
    }

    private async Task PollAsync(TrackedRun tracked)
    {
        while (!tracked.Finished && !_disposed)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await ResyncAsync(tracked);
        }
    }

    private async Task OnProgressAsync(TrackedRun tracked, RunProgressDto progress)
    {
        if (tracked.Finished)
            return;

        if (progress.Status is RunStatus.Complete or RunStatus.Failed)
        {
            // The final word comes from the stored run: it carries the notes and every output.
            await ResyncAsync(tracked);
            return;
        }

        (tracked.Status, tracked.Step, tracked.Percent, tracked.Message) = (progress.Status, progress.Step, progress.Percent, progress.Message);
        if (progress.Output is { } output && tracked.Outputs.All(o => o.Id != output.Id))
            tracked.Outputs.Add(output);
        Changed?.Invoke();
    }

    private async Task ResyncAsync(TrackedRun tracked)
    {
        if (tracked.Finished || _disposed)
            return;

        RunDto? current;
        try
        {
            current = await runs.GetAsync(tracked.Run.Id);
        }
        catch (HttpRequestException)
        {
            return;
        }

        if (current is null || tracked.Finished)
            return;

        tracked.Run = current;
        tracked.Notes = current.Notes;
        tracked.Step = current.CurrentStep;
        if (current.Error is not null)
            tracked.Message = current.Error;
        await LoadOutputsAsync(tracked);

        var ended = current.Status is RunStatus.Complete or RunStatus.Failed;
        tracked.Status = current.Status;
        if (ended)
        {
            tracked.Percent = current.Status == RunStatus.Complete ? 100 : tracked.Percent;
            if (tracked.Subscription is { } subscription)
            {
                tracked.Subscription = null;
                await subscription.DisposeAsync();
            }

            Finished?.Invoke(tracked);
        }

        Changed?.Invoke();
    }

    private async Task LoadOutputsAsync(TrackedRun tracked)
    {
        foreach (var id in tracked.Run.OutputIds.Where(id => tracked.Outputs.All(o => o.Id != id)).ToList())
        {
            // An output the user has since deleted is simply not shown.
            if (await media.GetAsync(id) is { } output && tracked.Outputs.All(o => o.Id != id))
                tracked.Outputs.Add(output);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var tracked in _runs)
        {
            if (tracked.Subscription is { } subscription)
                await subscription.DisposeAsync();
        }
    }
}
