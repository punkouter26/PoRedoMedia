using System.Collections.Concurrent;
using System.Diagnostics;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Runs;

/// <summary>
/// The queue runs wait in. One run executes at a time per process, which is what the hosting
/// plan's CPU budget can carry, and one source can have only one run queued or running. Owners
/// take turns. The run rows are the durable copy of the queue: what a restart cuts short is
/// queued again when the next process starts.
/// </summary>
public sealed partial class RunDispatcher(RunExecutor executor, IRunRepository runs, StorageClients storage, ILogger<RunDispatcher> logger)
    : BackgroundService
{
    /// <summary>
    /// How many times a run may be started. One cut short this often is failed instead of tried
    /// again, in case it is the run that brings the process down.
    /// </summary>
    public const int MaxAttempts = 3;

    private readonly FairRunQueue _queue = new();
    private readonly ConcurrentDictionary<MediaId, byte> _busySources = new();
    private readonly ConcurrentDictionary<RunId, CancellationTokenSource> _cancels = new();
    private readonly TaskCompletionSource _recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} was cut short by a restart; queued again, attempt {Attempt}")]
    private partial void LogResumed(RunId runId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unfinished runs could not be recovered; the housekeeping sweep will fail them")]
    private partial void LogRecoveryFailed(Exception exception);

    public override void Dispose()
    {
        base.Dispose();
        _queue.Dispose();
    }

    /// <summary>Done once the runs an earlier process left unfinished have been queued again or failed.</summary>
    public Task Recovered => _recovered.Task;

    /// <summary>Claims the source's lane. False when it already has a run queued or running.</summary>
    public bool TryReserve(MediaId source) => _busySources.TryAdd(source, 0);

    public void Release(MediaId source) => _busySources.TryRemove(source, out _);

    public bool IsBusy(MediaId source) => _busySources.ContainsKey(source);

    /// <summary>Queues a run whose lane the caller has already reserved.</summary>
    public void Queue(Run run)
    {
        _cancels[run.Id] = new CancellationTokenSource();
        _queue.Add(run);
    }

    /// <summary>
    /// Queues again every run an earlier process left unfinished, whether a deploy, a crash or the
    /// plan unloading an idle app ended it. Each resumes at the step it had reached.
    /// </summary>
    internal async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            if (!storage.IsConfigured)
                return;

            foreach (var run in (await runs.ListUnfinishedAsync(ct)).OrderBy(r => r.CreatedAt))
            {
                // Taken already: queued by this process, which is not what is being recovered.
                if (!TryReserve(run.SourceId))
                    continue;

                if (run.Attempts >= MaxAttempts || !run.Functions.All(executor.Available.Contains))
                {
                    Release(run.SourceId);
                    await executor.AbandonAsync(run, run.Attempts >= MaxAttempts
                        ? "Interrupted by server restarts too many times."
                        : "Interrupted by a server restart, after which this function was no longer available.");
                    continue;
                }

                var again = run with { Status = RunStatus.Queued, Attempts = run.Attempts + 1 };
                await runs.SaveAsync(again, ct);
                LogResumed(run.Id, again.Attempts);
                PoTelemetry.RunsResumed.Add(1);
                Queue(again);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogRecoveryFailed(ex);
        }
        finally
        {
            _recovered.TrySetResult();
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
        await RecoverAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var run = await _queue.TakeAsync(stoppingToken);
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
        using var activity = PoTelemetry.Source.StartActivity("run");
        activity?.SetTag("run.id", run.Id.ToString());
        activity?.SetTag("run.functions", string.Join(',', run.Functions));
        activity?.SetTag("run.attempt", run.Attempts);
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
        var log = new List<string>(run.Log);
        var logged = log.Sum(l => l.Length);

        // A run picked up again after a restart skips the steps it had finished, and carries on
        // from the picture it had reached. The step it was in starts over.
        var working = source;
        if (run.Attempts > 1 && run.CurrentStep is { } reached)
        {
            done.UnionWith(ordered.TakeWhile(f => f != reached));
            if (run.WorkingId is { } workingId && await media.GetAsync(run.Owner, workingId, ct) is { } found)
                working = found;
            await OnMessageAsync("Picked up again after a server restart");
        }

        var context = new RunContext(run, source, OnOutputAsync, OnMessageAsync, working);

        foreach (var function in ordered)
        {
            if (done.Contains(function))
                continue;

            var step = steps.First(s => s.Handles.Contains(function));
            run = run with { Status = RunStatus.Running, CurrentStep = function, Log = [.. log] };
            await runs.SaveAsync(run, ct);
            await OnMessageAsync(FunctionStack.Label(function));

            using var activity = PoTelemetry.Source.StartActivity($"step {function}");
            var started = Stopwatch.GetTimestamp();
            try
            {
                await step.ExecuteAsync(context, ct);
                Measure("ok");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Measure("cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Measure("failed");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                // Outputs made by earlier steps stay in the gallery; the run says where it stopped.
                if (ex is not RunStepException)
                    logger.LogError(ex, "Run {RunId} failed in {Function}", run.Id, function);
                // A render that overran its time limit carries a message written for the user too.
                var reason = ex is RunStepException or TimeoutException ? ex.Message : $"{FunctionStack.Label(function)} failed unexpectedly.";
                await FailAsync(run with { Status = RunStatus.Failed, Error = reason, Notes = [.. context.Notes], Log = [.. log] }, ct);
                return;
            }

            done.UnionWith(step.Handles);

            void Measure(string outcome) => PoTelemetry.StepSeconds.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new("function", function.ToString()), new("outcome", outcome));
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

        async Task OnOutputAsync(MediaItem output, bool becomesCurrent)
        {
            run = run with
            {
                OutputIds = [.. run.OutputIds, output.Id],
                WorkingId = becomesCurrent ? output.Id : run.WorkingId,
                Log = [.. log],
            };
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
        PoTelemetry.RunsFinished.Add(1, new("status", run.Status.ToString()), new("functions", string.Join(',', run.Functions)));
        await notifier.ProgressAsync(
            new(run.Id.Value, run.Status, run.CurrentStep, run.Status == RunStatus.Complete ? 100 : 0, run.Error ?? "Done"), ct);
    }
}
