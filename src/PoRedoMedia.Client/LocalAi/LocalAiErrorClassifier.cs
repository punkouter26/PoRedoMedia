namespace PoRedoMedia.Client.LocalAi;

/// <summary>Category of a local-inference failure.</summary>
public enum LocalAiFailure
{
    /// <summary>Unrecognised failure.</summary>
    Unknown = 0,

    /// <summary>GPU or system memory exhausted.</summary>
    OutOfMemory = 1,

    /// <summary>The WebGPU device was lost — usually OOM elsewhere, or a stale context.</summary>
    DeviceLost = 2,

    /// <summary>Shader compilation failed on this GPU/driver.</summary>
    ShaderCompilation = 3,

    /// <summary>The model needs <c>shader-f16</c> and the adapter does not have it.</summary>
    ShaderF16Unsupported = 4,

    /// <summary>No WebGPU adapter is available at all.</summary>
    NoWebGpuAdapter = 5,

    /// <summary>Weights or runtime could not be downloaded.</summary>
    Network = 6,

    /// <summary>The runtime does not support this model.</summary>
    ModelUnsupported = 7,
}

/// <summary>
/// Turns cryptic WebGPU/ONNX messages into a category the fallback logic can act on and a sentence
/// a user can act on.
/// </summary>
/// <remarks>
/// Ported from the WebLLM worker in the PoLocalCompare project, where these exact strings were
/// found to be what browsers actually emit, and extended for the ONNX/wasm backend.
/// </remarks>
public static class LocalAiErrorClassifier
{
    /// <summary>Categorises a raw runtime error message.</summary>
    public static LocalAiFailure Classify(string? rawMessage)
    {
        var m = (rawMessage ?? string.Empty).ToLowerInvariant();

        // Order matters: a device-lost message often also contains "memory", and the shader-f16
        // case must be distinguished from generic shader failures because only it is fixed by
        // dropping to a non-f16 variant.
        if (m.Contains("shader-f16") || m.Contains("shader f16") || m.Contains("f16 is not supported"))
            return LocalAiFailure.ShaderF16Unsupported;

        if (m.Contains("device was lost") || m.Contains("device is lost") || m.Contains("device lost")
            || m.Contains("external instance reference"))
            return LocalAiFailure.DeviceLost;

        if (m.Contains("out of memory") || m.Contains("oom") || m.Contains("vram")
            || m.Contains("failed to allocate") || m.Contains("allocation failed"))
            return LocalAiFailure.OutOfMemory;

        if (m.Contains("requestadapter") || m.Contains("no adapter") || m.Contains("adapter is null")
            || (m.Contains("webgpu") && m.Contains("not")))
            return LocalAiFailure.NoWebGpuAdapter;

        if (m.Contains("shader") || m.Contains("compil"))
            return LocalAiFailure.ShaderCompilation;

        if (m.Contains("failed to fetch") || m.Contains("networkerror") || m.Contains("err_")
            || m.Contains("load failed") || m.Contains("importscripts"))
            return LocalAiFailure.Network;

        if (m.Contains("model_lib") || m.Contains("model lib") || m.Contains("cannot find model")
            || m.Contains("unsupported model"))
            return LocalAiFailure.ModelUnsupported;

        return LocalAiFailure.Unknown;
    }

    /// <summary>An actionable explanation for the user.</summary>
    public static string Describe(LocalAiFailure failure, string? rawMessage = null) => failure switch
    {
        LocalAiFailure.OutOfMemory =>
            "Ran out of memory loading the model. Close other tabs and try again, or pick a smaller model.",
        LocalAiFailure.DeviceLost =>
            "The GPU context was lost. A hard refresh (Ctrl+Shift+R) clears it; running one model at a time helps.",
        LocalAiFailure.ShaderCompilation =>
            "Your GPU or driver couldn't compile this model's shaders.",
        LocalAiFailure.ShaderF16Unsupported =>
            "Your GPU lacks the shader-f16 feature this variant needs.",
        LocalAiFailure.NoWebGpuAdapter =>
            "WebGPU isn't available in this browser. Use desktop Chrome or Edge with hardware acceleration on.",
        LocalAiFailure.Network =>
            "Couldn't download the model. Weights stream from HuggingFace on first run — check your connection.",
        LocalAiFailure.ModelUnsupported =>
            "This model isn't supported by the in-browser runtime.",
        _ => string.IsNullOrWhiteSpace(rawMessage)
            ? "Local inference failed for an unknown reason."
            : $"Local inference failed: {rawMessage}",
    };
}
