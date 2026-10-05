namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// What the current browser/GPU can actually run. Produced by probing
/// <c>navigator.gpu.requestAdapter()</c> from JS; modelled as a plain record so every consumer
/// of it — above all <see cref="DtypeChain"/> — stays testable with no browser involved.
/// </summary>
/// <param name="HasWebGpu">A WebGPU adapter was obtained.</param>
/// <param name="HasShaderF16">The adapter reports the <c>shader-f16</c> feature.</param>
/// <param name="MaxBufferBytes">
/// Largest single buffer the adapter allows, or null when unknown. Used only for reporting; the
/// chain does not gate on it, because a too-small buffer surfaces as a load failure that the
/// advance-on-failure path already handles.
/// </param>
/// <param name="AdapterDescription">Vendor/architecture string for diagnostics, when available.</param>
public sealed record DeviceCapabilities(
    bool HasWebGpu,
    bool HasShaderF16,
    long? MaxBufferBytes = null,
    string? AdapterDescription = null)
{
    /// <summary>No WebGPU at all — CPU/wasm execution only.</summary>
    public static DeviceCapabilities None { get; } = new(HasWebGpu: false, HasShaderF16: false);

    /// <summary>
    /// The device to run on. WebGPU when present, otherwise wasm — there is no third option, and
    /// a missing adapter must never be treated as a hard failure.
    /// </summary>
    public LocalDevice Device => HasWebGpu ? LocalDevice.WebGpu : LocalDevice.Wasm;
}
