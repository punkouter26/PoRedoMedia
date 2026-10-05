namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// The dtype fallback chain.
/// </summary>
/// <remarks>
/// Resolution is two-phase, and both phases live here rather than in either JS runtime — that is
/// what stops two runtimes from becoming two policies, and it is what makes the whole mechanism
/// unit-testable with no browser and no network:
/// <list type="number">
/// <item>
///   <b>Probe-time pruning</b> — variants the device cannot run are removed up front
///   (<see cref="Prune"/>). Attempting an fp16 variant on an adapter without <c>shader-f16</c>
///   only ever produces a shader-compilation failure.
/// </item>
/// <item>
///   <b>Load-time advance</b> — an out-of-memory, shader, or device-lost failure moves to the next
///   surviving variant (<see cref="Advance"/>). Exhausting the chain is a hard failure.
/// </item>
/// </list>
/// A runtime adapter only ever <em>interprets</em> the variant this class hands it.
/// </remarks>
public sealed class DtypeChain
{
    private readonly IReadOnlyList<DtypeVariant> _variants;
    private int _index;

    private DtypeChain(IReadOnlyList<DtypeVariant> variants, LocalDevice device)
    {
        _variants = variants;
        Device = device;
    }

    /// <summary>Backend these variants will run on.</summary>
    public LocalDevice Device { get; }

    /// <summary>The variants that survived pruning, in preference order.</summary>
    public IReadOnlyList<DtypeVariant> Variants => _variants;

    /// <summary>The variant to attempt now.</summary>
    /// <exception cref="InvalidOperationException">The chain is exhausted.</exception>
    public DtypeVariant Current => _index < _variants.Count
        ? _variants[_index]
        : throw new InvalidOperationException(
            "The dtype chain is exhausted — check IsExhausted before reading Current.");

    /// <summary>True when every variant has been tried and failed.</summary>
    public bool IsExhausted => _index >= _variants.Count;

    /// <summary>How many variants have been attempted, including the current one.</summary>
    public int AttemptNumber => Math.Min(_index + 1, _variants.Count);

    /// <summary>Total variants available after pruning.</summary>
    public int Length => _variants.Count;

    /// <summary>
    /// Builds a chain for <paramref name="descriptor"/> on <paramref name="capabilities"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Pruning removed every variant — the model cannot run on this device at all, which is a
    /// registry problem (no universally-supported variant was declared) rather than a runtime one.
    /// </exception>
    public static DtypeChain Create(LocalModelDescriptor descriptor, DeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(capabilities);

        var pruned = Prune(descriptor.VariantChain, capabilities);

        if (pruned.Count == 0)
        {
            throw new InvalidOperationException(
                $"No dtype variant of '{descriptor.Id}' can run on this device "
                + $"(WebGPU={capabilities.HasWebGpu}, shader-f16={capabilities.HasShaderF16}). "
                + "Every registry entry must declare at least one variant that survives pruning "
                + "on the weakest supported device — Q4 or F32.");
        }

        return new DtypeChain(pruned, capabilities.Device);
    }

    /// <summary>
    /// Removes variants the device cannot execute, preserving the declared order.
    /// </summary>
    /// <remarks>
    /// fp16 variants need the <c>shader-f16</c> GPU feature. Without WebGPU entirely, execution
    /// falls to wasm, where only the integer-quantized and full-precision paths are available.
    /// </remarks>
    public static IReadOnlyList<DtypeVariant> Prune(
        IReadOnlyList<DtypeVariant> variants, DeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(capabilities);

        return [.. variants.Where(v => IsSupported(v, capabilities))];
    }

    private static bool IsSupported(DtypeVariant variant, DeviceCapabilities capabilities) => variant switch
    {
        // Half-precision compute requires the GPU feature; it exists nowhere on the wasm backend.
        DtypeVariant.Q4F16 or DtypeVariant.F16 => capabilities.HasWebGpu && capabilities.HasShaderF16,

        // Integer-quantized and full-precision run on both backends.
        DtypeVariant.Q4 or DtypeVariant.F32 => true,

        _ => false,
    };

    /// <summary>
    /// Moves to the next variant after a load failure.
    /// </summary>
    /// <returns><c>true</c> if another variant is available; <c>false</c> if the chain is now exhausted.</returns>
    public bool Advance()
    {
        if (IsExhausted) return false;

        _index++;
        return !IsExhausted;
    }

    /// <summary>
    /// Whether <paramref name="failure"/> is worth retrying with a smaller variant.
    /// </summary>
    /// <remarks>
    /// Only resource and compilation failures are: a smaller variant genuinely might fit or compile.
    /// A network failure or an unsupported-model error would fail identically at every variant, so
    /// retrying just burns the user's bandwidth and time.
    /// </remarks>
    public static bool IsRecoverable(LocalAiFailure failure) => failure switch
    {
        LocalAiFailure.OutOfMemory or LocalAiFailure.DeviceLost or LocalAiFailure.ShaderCompilation
            or LocalAiFailure.ShaderF16Unsupported => true,
        _ => false,
    };

    /// <summary>Renders the chain for logs and the diagnostics panel, marking the current variant.</summary>
    public override string ToString()
    {
        var parts = _variants.Select((v, i) => i == _index ? $"[{v}]" : v.ToString());
        return $"{Device}: {string.Join(" → ", parts)}";
    }
}
