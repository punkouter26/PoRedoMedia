using System.Collections.Concurrent;
using System.Threading.Channels;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Runs;

/// <summary>
/// The queue runs wait in. One run executes at a time per process, which is what the hosting
/// plan's CPU budget can carry, and one source can have only one run queued or running.
/// </summary>
public sealed class RunDispatcher(RunExecutor executor, ILogger<RunDispatcher> logger) : BackgroundService
{
    private readonly Channel<Run> _queue = Channel.CreateUnbounded<Run>();
    private readonly ConcurrentDictionary<MediaId, byte> _busySources = new();
    private readonly ConcurrentDictionary<RunId, CancellationTokenSource> _cancels = new();

    /// <summary>Claims the source's lane. False when it already has a run queued or running.</summary>
    public bool TryReserve(MediaId source) => _busySources.TryAdd(source, 0);

    public void Release(MediaId source) => _busySources.TryRemove(source, out _);

    public bool IsBusy(MediaId source) => _busySources.ContainsKey(source);

    /// <summary>Queues a run whose lane the caller has already reserved.</summary>
    public void Queue(Run run)
    {
        _cancels[run.Id] = new CancellationTokenSource();
        // The channel is unbounded, so a write only fails once the host is stopping.
        if (!_queue.Writer.TryWrite(run))
        {
            Release(run.SourceId);
            _cancels.TryRemove(run.Id, out _);
        }
    }

    /// <summary>Asks a queued or running run to stop. False when it has already ended.</summary>
    // ponytail: a queued run is ended when its turn comes, not at once. End it here if waiting
    // behind a long run to see "Cancelled" ever matters.
    public bool Cancel(RunId id)
    {
        if (!_cancels.TryGetValue(id, out var cancel))
            return false;

        try
        {
            cancel.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // It ended between the lookup and the call.
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var run in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                _cancels.TryGetValue(run.Id, out var cancel);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancel?.Token ?? CancellationToken.None);
                try
                {
                    try
                    {
                        await executor.ExecuteAsync(run, stop.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        await executor.AbandonAsync(run, "Cancelled.");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Run {RunId} could not be recorded", run.Id);
                }
                finally
                {
                    Release(run.SourceId);
                    if (_cancels.TryRemove(run.Id, out var done))
                        done.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }
}

/// <summary>Runs the steps of one run in the fixed order and records what happened.</summary>
public sealed class RunExecutor(
    IEnumerable<IRunStep> steps, IRunRepository runs, IMediaRepository media, IRunNotifier notifier, IRenderQuota quota,
    ILogger<RunExecutor> logger)
{
    /// <summary>
    /// The log is stored in one table property, which holds 32,000 characters. These keep it well
    /// inside that however chatty a step is; lines past the limit are still sent live.
    /// </summary>
    private const int MaxLogLine = 240;
    private const int MaxLogCharacters = 12_000;

    /// <summary>The functions that have a step registered, and so can be run.</summary>
    public IReadOnlySet<MediaFunction> Available { get; } = steps.SelectMany(s => s.Handles).ToHashSet();

    public async Task ExecuteAsync(Run run, CancellationToken ct)
    {
        try
        {
            await ExecuteStepsAsync(run, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Recording the outcome failed (a storage blip). One more attempt, so the run is not
            // left showing "Running" until the next restart. Outputs are read back, not guessed.
            var latest = await runs.GetAsync(run.Owner, run.Id, CancellationToken.None) ?? run;
            if (latest.Status is RunStatus.Queued or RunStatus.Running)
                await runs.SaveAsync(latest with { Status = RunStatus.Failed, Error = "The run could not be recorded. Try again." }, CancellationToken.None);
            throw;
        }
    }

    private async Task ExecuteStepsAsync(Run run, CancellationToken ct)
    {
        var source = await media.GetAsync(run.Owner, run.SourceId, ct);
        if (source is null)
        {
            await FailAsync(run with { Status = RunStatus.Failed, Error = "The source was deleted before the run started." }, ct);
            return;
        }

        var ordered = FunctionStack.InRunOrder(run.Functions);
        var done = new HashSet<MediaFunction>();
        var log = new List<string>();
        var logged = 0;
        var context = new RunContext(run, source, OnOutputAsync, OnMessageAsync);

        foreach (var function in ordered)
        {
            if (done.Contains(function))
                continue;

            var step = steps.First(s => s.Handles.Contains(function));
            run = run with { Status = RunStatus.Running, CurrentStep = function, Log = [.. log] };
            await runs.SaveAsync(run, ct);
            await OnMessageAsync(FunctionStack.Label(function));

            try
            {
                await step.ExecuteAsync(context, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Outputs made by earlier steps stay in the gallery; the run says where it stopped.
                if (ex is not RunStepException)
                    logger.LogError(ex, "Run {RunId} failed in {Function}", run.Id, function);
                // A render that overran its time limit carries a message written for the user too.
                var reason = ex is RunStepException or TimeoutException ? ex.Message : $"{FunctionStack.Label(function)} failed unexpectedly.";
                await FailAsync(run with { Status = RunStatus.Failed, Error = reason, Notes = [.. context.Notes], Log = [.. log] }, ct);
                return;
            }

            done.UnionWith(step.Handles);
        }

        await FinishAsync(run with { Status = RunStatus.Complete, CurrentStep = null, Notes = [.. context.Notes], Log = [.. log] }, ct);

        int Percent() => ordered.Count == 0 ? 0 : (int)(100.0 * done.Count(ordered.Contains) / ordered.Count);

        Task OnMessageAsync(string message)
        {
            if (logged + Math.Min(message.Length, MaxLogLine) <= MaxLogCharacters)
            {
                log.Add(message.Length > MaxLogLine ? message[..MaxLogLine] : message);
                logged += log[^1].Length;
            }

            return notifier.ProgressAsync(new(run.Id.Value, RunStatus.Running, run.CurrentStep, Percent(), message), ct);
        }

        async Task OnOutputAsync(MediaItem output)
        {
            run = run with { OutputIds = [.. run.OutputIds, output.Id], Log = [.. log] };
            await runs.SaveAsync(run, ct);
            await notifier.ProgressAsync(
                new(run.Id.Value, RunStatus.Running, run.CurrentStep, Percent(), "Result ready", output.ToDto()), ct);
        }
    }

    /// <summary>Ends a run that was stopped from outside its steps: cancelled by its owner.</summary>
    public async Task AbandonAsync(Run run, string reason)
    {
        // Read back, so results the run had already made are kept and counted.
        var latest = await runs.GetAsync(run.Owner, run.Id, CancellationToken.None) ?? run;
        if (latest.Status is RunStatus.Queued or RunStatus.Running)
            await FailAsync(latest with { Status = RunStatus.Failed, Error = reason }, CancellationToken.None);
    }

    /// <summary>Ends a failed run. One that made nothing gives its credit back and says so.</summary>
    private async Task FailAsync(Run run, CancellationToken ct)
    {
        if (run.OutputIds.Count == 0)
        {
            await quota.RefundAsync(run.Owner, ct);
            run = run with { Error = $"{run.Error} It was not charged." };
        }

        await FinishAsync(run, ct);
    }

    private async Task FinishAsync(Run run, CancellationToken ct)
    {
        await runs.SaveAsync(run, ct);
        await notifier.ProgressAsync(
            new(run.Id.Value, run.Status, run.CurrentStep, run.Status == RunStatus.Complete ? 100 : 0, run.Error ?? "Done"), ct);
    }
}
