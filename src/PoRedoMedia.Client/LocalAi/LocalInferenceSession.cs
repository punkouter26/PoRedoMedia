using Microsoft.Extensions.Logging;

namespace PoRedoMedia.Client.LocalAi;

/// <summary>Progress of a local run, surfaced to the UI.</summary>
/// <param name="Stage">Coarse phase, for choosing the message and spinner.</param>
/// <param name="Detail">Runtime-supplied text, e.g. which shard is downloading.</param>
/// <param name="LoadPercent">0-100 while weights download, null once running.</param>
/// <param name="Variant">Variant currently being attempted.</param>
public sealed record LocalInferenceStatus(
    LocalStage Stage,
    string? Detail = null,
    int? LoadPercent = null,
    DtypeVariant? Variant = null);

/// <summary>Phase of a local run.</summary>
public enum LocalStage
{
    Probing = 0,
    Downloading = 1,
    Loading = 2,
    Running = 3,
    Done = 4,
    Failed = 5,
}

/// <summary>Executes one attempt at a given variant. Implemented over JS interop.</summary>
public interface ILocalInferenceRuntime
{
    /// <summary>Runtime this implementation serves.</summary>
    LocalRuntime Runtime { get; }

    /// <summary>
    /// Runs one attempt. Throws <see cref="LocalInferenceException"/> on failure so the caller can
    /// categorise it and decide whether to advance the chain.
    /// </summary>
    Task<string> RunAsync(
        LocalModelDescriptor descriptor,
        DtypeVariant variant,
        LocalDevice device,
        string prompt,
        byte[]? image,
        IProgress<LocalInferenceStatus>? progress,
        CancellationToken ct);
}

/// <summary>A local inference attempt failed, carrying the classified cause.</summary>
public sealed class LocalInferenceException(LocalAiFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public LocalAiFailure Failure { get; } = failure;
}

/// <summary>
/// Drives a <see cref="DtypeChain"/> across one or more runtime attempts.
/// </summary>
/// <remarks>
/// This is the advance-on-failure half, and the reason it lives in C# rather than
/// in either worker: the policy is identical for both runtimes and is fully testable with a fake
/// <see cref="ILocalInferenceRuntime"/>.
/// </remarks>
public sealed class LocalInferenceSession(ILocalInferenceRuntime runtime, ILogger logger)
{
    /// <summary>
    /// Runs <paramref name="descriptor"/>, stepping down the pruned chain on recoverable failures.
    /// </summary>
    /// <exception cref="LocalInferenceException">
    /// The chain was exhausted, or a failure occurred that a smaller variant cannot fix.
    /// </exception>
    public async Task<LocalInferenceOutcome> RunAsync(
        LocalModelDescriptor descriptor,
        DeviceCapabilities capabilities,
        string prompt,
        byte[]? image = null,
        IProgress<LocalInferenceStatus>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var chain = DtypeChain.Create(descriptor, capabilities);
        logger.LogInformation("Local inference starting. Model={Model}, Chain={Chain}", descriptor.Id, chain);

        LocalInferenceException? last = null;

        while (!chain.IsExhausted)
        {
            var variant = chain.Current;
            progress?.Report(new LocalInferenceStatus(LocalStage.Loading, Variant: variant));

            try
            {
                var text = await runtime.RunAsync(
                    descriptor, variant, chain.Device, prompt, image, progress, ct);

                logger.LogInformation(
                    "Local inference succeeded. Model={Model}, Variant={Variant}, Attempt={Attempt}/{Total}",
                    descriptor.Id, variant, chain.AttemptNumber, chain.Length);

                progress?.Report(new LocalInferenceStatus(LocalStage.Done, Variant: variant));
                return new LocalInferenceOutcome(text, variant, chain.Device, chain.AttemptNumber);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LocalInferenceException ex)
            {
                last = ex;

                if (!DtypeChain.IsRecoverable(ex.Failure))
                {
                    // A network or unsupported-model failure repeats identically at every variant.
                    // Retrying would only spend the user's bandwidth to reach the same place.
                    logger.LogWarning(
                        "Local inference failed unrecoverably. Model={Model}, Variant={Variant}, Failure={Failure}",
                        descriptor.Id, variant, ex.Failure);
                    progress?.Report(new LocalInferenceStatus(LocalStage.Failed, ex.Message, Variant: variant));
                    throw;
                }

                logger.LogWarning(
                    "Variant {Variant} failed ({Failure}); advancing the chain. Model={Model}",
                    variant, ex.Failure, descriptor.Id);

                if (!chain.Advance()) break;
            }
        }

        var message =
            $"Every dtype variant of '{descriptor.DisplayName}' failed on this device "
            + $"({chain.Length} attempted). {LocalAiErrorClassifier.Describe(last?.Failure ?? LocalAiFailure.Unknown, last?.Message)}";

        logger.LogWarning("Local inference exhausted the chain. Model={Model}", descriptor.Id);
        progress?.Report(new LocalInferenceStatus(LocalStage.Failed, message));

        throw new LocalInferenceException(last?.Failure ?? LocalAiFailure.Unknown, message, last);
    }
}

/// <summary>A successful local run and how it was achieved.</summary>
/// <param name="Text">Model output.</param>
/// <param name="Variant">Variant that succeeded — may not be the preferred one.</param>
/// <param name="Device">Backend used.</param>
/// <param name="Attempts">How many variants were tried, including the successful one.</param>
public sealed record LocalInferenceOutcome(string Text, DtypeVariant Variant, LocalDevice Device, int Attempts);
