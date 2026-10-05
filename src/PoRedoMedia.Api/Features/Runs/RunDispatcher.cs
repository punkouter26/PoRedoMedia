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

    /// <summary>Claims the source's lane. False when it already has a run queued or running.</summary>
    public bool TryReserve(MediaId source) => _busySources.TryAdd(source, 0);

    public void Release(MediaId source) => _busySources.TryRemove(source, out _);

    public bool IsBusy(MediaId source) => _busySources.ContainsKey(source);

    /// <summary>Queues a run whose lane the caller has already reserved.</summary>
    public void Queue(Run run)
    {
        // The channel is unbounded, so a write only fails once the host is stopping.
        if (!_queue.Writer.TryWrite(run))
            Release(run.SourceId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var run in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await executor.ExecuteAsync(run, stoppingToken);
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
    IEnumerable<IRunStep> steps, IRunRepository runs, IMediaRepository media, IRunNotifier notifier, ILogger<RunExecutor> logger)
{
    /// <summary>The functions that have a step registered, and so can be run.</summary>
    public IReadOnlySet<MediaFunction> Available { get; } = steps.SelectMany(s => s.Handles).ToHashSet();

    public async Task ExecuteAsync(Run run, CancellationToken ct)
    {
        var source = await media.GetAsync(run.Owner, run.SourceId, ct);
        if (source is null)
        {
            await FinishAsync(run with { Status = RunStatus.Failed, Error = "The source was deleted before the run started." }, ct);
            return;
        }

        var ordered = FunctionStack.InRunOrder(run.Functions);
        var done = new HashSet<MediaFunction>();
        var context = new RunContext(run, source, OnOutputAsync, OnMessageAsync);

        foreach (var function in ordered)
        {
            if (done.Contains(function))
                continue;

            var step = steps.First(s => s.Handles.Contains(function));
            run = run with { Status = RunStatus.Running, CurrentStep = function };
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
                var reason = ex is RunStepException ? ex.Message : $"{FunctionStack.Label(function)} failed unexpectedly.";
                await FinishAsync(run with { Status = RunStatus.Failed, Error = reason, Notes = [.. context.Notes] }, ct);
                return;
            }

            done.UnionWith(step.Handles);
        }

        await FinishAsync(run with { Status = RunStatus.Complete, CurrentStep = null, Notes = [.. context.Notes] }, ct);

        int Percent() => ordered.Count == 0 ? 0 : (int)(100.0 * done.Count(ordered.Contains) / ordered.Count);

        Task OnMessageAsync(string message) =>
            notifier.ProgressAsync(new(run.Id.Value, RunStatus.Running, run.CurrentStep, Percent(), message), ct);

        async Task OnOutputAsync(MediaItem output)
        {
            run = run with { OutputIds = [.. run.OutputIds, output.Id] };
            await runs.SaveAsync(run, ct);
            await notifier.ProgressAsync(
                new(run.Id.Value, RunStatus.Running, run.CurrentStep, Percent(), "Result ready", output.ToDto()), ct);
        }
    }

    private async Task FinishAsync(Run run, CancellationToken ct)
    {
        await runs.SaveAsync(run, ct);
        await notifier.ProgressAsync(
            new(run.Id.Value, run.Status, run.CurrentStep, run.Status == RunStatus.Complete ? 100 : 0, run.Error ?? "Done"), ct);
    }
}
