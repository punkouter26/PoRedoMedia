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
    private readonly Dictionary<LocalRuntime, JsLocalInferenceRuntime> _runtimes = [];
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

    /// <summary>Describes an image with the default local vision model.</summary>
    public Task<LocalInferenceOutcome> DescribeImageAsync(
        byte[] image,
        string? prompt = null,
        IProgress<LocalInferenceStatus>? progress = null,
        CancellationToken ct = default)
        => RunAsync(LocalCapability.Vision, prompt ?? string.Empty, image, progress, ct);

    /// <summary>Completes a text prompt with the default local text model.</summary>
    public Task<LocalInferenceOutcome> CompleteTextAsync(
        string prompt,
        IProgress<LocalInferenceStatus>? progress = null,
        CancellationToken ct = default)
        => RunAsync(LocalCapability.Text, prompt, image: null, progress, ct);

    private async Task<LocalInferenceOutcome> RunAsync(
        LocalCapability capability,
        string prompt,
        byte[]? image,
        IProgress<LocalInferenceStatus>? progress,
        CancellationToken ct)
    {
        var descriptor = LocalModelRegistry.DefaultFor(capability)
            ?? throw new InvalidOperationException($"No local model is registered for {capability}.");

        progress?.Report(new LocalInferenceStatus(LocalStage.Probing));
        var capabilities = await GetCapabilitiesAsync();

        var session = new LocalInferenceSession(
            GetRuntime(descriptor.Runtime),
            loggerFactory.CreateLogger($"LocalAi.{descriptor.Runtime}"));

        return await session.RunAsync(descriptor, capabilities, prompt, image, progress, ct);
    }

    private JsLocalInferenceRuntime GetRuntime(LocalRuntime runtime)
    {
        if (_runtimes.TryGetValue(runtime, out var existing)) return existing;

        var created = new JsLocalInferenceRuntime(js, runtime);
        _runtimes[runtime] = created;
        return created;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _runtimes.Values)
        {
            await runtime.DisposeAsync();
        }

        _runtimes.Clear();
    }

    /// <summary>Wire shape of the JS device probe.</summary>
    private sealed record DeviceProbe(
        bool HasWebGpu, bool HasShaderF16, long? MaxBufferBytes, string? AdapterDescription);
}
