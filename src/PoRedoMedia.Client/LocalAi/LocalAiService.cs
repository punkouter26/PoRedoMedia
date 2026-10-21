using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// Entry point the UI uses for browser-native inference.
/// </summary>
/// <remarks>
/// Probes the device once per page load and caches it — <c>requestAdapter()</c> is not free, and
/// the answer cannot change without a reload.
/// </remarks>
public sealed class LocalAiService(IJSRuntime js, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private JsLocalInferenceRuntime? _runtime;
    private DeviceCapabilities? _capabilities;

    /// <summary>
    /// Device capabilities, probed on first use. Returns <see cref="DeviceCapabilities.None"/> if
    /// the probe fails, so a JS error degrades to wasm rather than breaking the feature.
    /// </summary>
    public async ValueTask<DeviceCapabilities> GetCapabilitiesAsync()
    {
        if (_capabilities is not null) return _capabilities;

        try
        {
            var probe = await js.InvokeAsync<DeviceProbe>("poLocalAiProbeDevice");
            _capabilities = new DeviceCapabilities(
                probe.HasWebGpu, probe.HasShaderF16, probe.MaxBufferBytes, probe.AdapterDescription);
        }
        catch (JSException)
        {
            _capabilities = DeviceCapabilities.None;
        }

        return _capabilities;
    }

    /// <summary>Describes an image with the local vision model.</summary>
    public async Task<LocalInferenceOutcome> DescribeImageAsync(
        byte[] image,
        string? prompt = null,
        IProgress<LocalInferenceStatus>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new LocalInferenceStatus(LocalStage.Probing));
        var capabilities = await GetCapabilitiesAsync();

        _runtime ??= new JsLocalInferenceRuntime(js);
        var session = new LocalInferenceSession(_runtime, loggerFactory.CreateLogger("LocalAi"));
        return await session.RunAsync(LocalModelRegistry.Vision, capabilities, prompt ?? string.Empty, image, progress, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_runtime is not null) await _runtime.DisposeAsync();
        _runtime = null;
    }

    /// <summary>Wire shape of the JS device probe.</summary>
    private sealed record DeviceProbe(
        bool HasWebGpu, bool HasShaderF16, long? MaxBufferBytes, string? AdapterDescription);
}
