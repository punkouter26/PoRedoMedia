using System.Collections.Concurrent;
using Microsoft.JSInterop;

namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// <see cref="ILocalInferenceRuntime"/> over the JS workers.
/// </summary>
/// <remarks>
/// Runs are keyed by a run id so several can be in flight without their callbacks crossing.
/// </remarks>
public sealed class JsLocalInferenceRuntime(IJSRuntime js) : ILocalInferenceRuntime, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RunState> _runs = new();
    private DotNetObjectReference<JsLocalInferenceRuntime>? _selfRef;

    public async Task<string> RunAsync(
        LocalModelDescriptor descriptor,
        DtypeVariant variant,
        LocalDevice device,
        string prompt,
        byte[]? image,
        IProgress<LocalInferenceStatus>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        _selfRef ??= DotNetObjectReference.Create(this);

        var runId = Guid.NewGuid().ToString("N");
        var state = new RunState(progress, variant);
        _runs[runId] = state;

        // Cancellation has to reach the worker: without terminating it, a cancelled run keeps
        // holding GPU memory and the next variant loads on top of it.
        await using var registration = ct.Register(() =>
        {
            _ = js.InvokeVoidAsync("poLocalAiCancel", runId).AsTask();
            state.Completion.TrySetCanceled(ct);
        });

        try
        {
            await js.InvokeVoidAsync(
                "poLocalAiRun", ct, _selfRef, runId, BuildOptions(descriptor, variant, device, prompt, image));

            return await state.Completion.Task.WaitAsync(ct);
        }
        catch (JSException ex)
        {
            // A dispatch failure (missing script, blocked worker) never reaches the callbacks, so
            // it has to be translated here or the chain would see no failure at all.
            var failure = LocalAiErrorClassifier.Classify(ex.Message);
            throw new LocalInferenceException(failure, LocalAiErrorClassifier.Describe(failure, ex.Message), ex);
        }
        finally
        {
            _runs.TryRemove(runId, out _);
        }
    }

    /// <summary>Builds the transformers.js worker payload.</summary>
    private static object BuildOptions(
        LocalModelDescriptor descriptor, DtypeVariant variant, LocalDevice device, string prompt, byte[]? image) => new
        {
            repoId = descriptor.RepoId,
            dtype = LocalModelRegistry.TransformersDtype(variant),
            device = device.ToString(),
            prompt,
            imageBase64 = image is null ? null : Convert.ToBase64String(image),
        };

    [JSInvokable]
    public void ReceiveStatus(string runId, string stage, string? detail, int? loadPercent)
    {
        if (!_runs.TryGetValue(runId, out var state)) return;

        var parsed = Enum.TryParse<LocalStage>(stage, ignoreCase: true, out var s) ? s : LocalStage.Loading;
        state.Progress?.Report(new LocalInferenceStatus(parsed, detail, loadPercent, state.Variant));
    }

    [JSInvokable]
    public void ReceiveComplete(string runId, string text)
    {
        if (_runs.TryGetValue(runId, out var state)) state.Completion.TrySetResult(text);
    }

    [JSInvokable]
    public void ReceiveError(string runId, string reason)
    {
        if (!_runs.TryGetValue(runId, out var state)) return;

        var failure = LocalAiErrorClassifier.Classify(reason);
        state.Completion.TrySetException(
            new LocalInferenceException(failure, LocalAiErrorClassifier.Describe(failure, reason)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runId in _runs.Keys)
        {
            try
            {
                await js.InvokeVoidAsync("poLocalAiCancel", runId);
            }
            catch (JSException)
            {
                // Disposal races page teardown; a failed cancel here is not actionable.
            }
        }

        _runs.Clear();
        _selfRef?.Dispose();
        _selfRef = null;
    }

    private sealed record RunState(IProgress<LocalInferenceStatus>? Progress, DtypeVariant Variant)
    {
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
