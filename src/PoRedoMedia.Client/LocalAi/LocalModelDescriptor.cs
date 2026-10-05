namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// Strongly-typed identifier for a registry entry.
/// </summary>
public readonly record struct LocalModelId
{
    private readonly string? _value;

    public LocalModelId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public string Value => _value ?? string.Empty;

    public override string ToString() => Value;
}

/// <summary>What a local model can do.</summary>
public enum LocalCapability
{
    /// <summary>Image in, caption/description out.</summary>
    Vision = 0,

    /// <summary>Text in, text out.</summary>
    Text = 1,
}

/// <summary>Which in-browser runtime executes a model.</summary>
/// <remarks>
/// Two runtimes exist because no single one covers both capabilities well: WebLLM is the proven
/// text path, transformers.js carries the vision models. They differ only in how they interpret a
/// <see cref="DtypeVariant"/> — the registry, the pruning, and the fallback policy are shared.
/// </remarks>
public enum LocalRuntime
{
    /// <summary>MLC WebLLM. Variant is expressed as a model-id suffix.</summary>
    WebLlm = 0,

    /// <summary>transformers.js / ONNX Runtime Web. Variant is a first-class <c>dtype</c> option.</summary>
    TransformersJs = 1,
}

/// <summary>
/// Quantization variant, ordered from smallest/fastest to largest/most accurate.
/// </summary>
public enum DtypeVariant
{
    /// <summary>4-bit. Smallest download, widest device support.</summary>
    Q4 = 0,

    /// <summary>4-bit weights with fp16 compute. Requires the <c>shader-f16</c> GPU feature.</summary>
    Q4F16 = 1,

    /// <summary>Half precision. Requires <c>shader-f16</c>.</summary>
    F16 = 2,

    /// <summary>Full precision. Largest and slowest, but the broadest compatibility.</summary>
    F32 = 3,
}

/// <summary>Execution backend for a run.</summary>
public enum LocalDevice
{
    /// <summary>GPU via WebGPU.</summary>
    WebGpu = 0,

    /// <summary>CPU via WebAssembly. The fallback when WebGPU is unavailable.</summary>
    Wasm = 1,
}

/// <summary>
/// One entry in the local model registry.
/// </summary>
/// <param name="Id">Stable internal id, used for routing and persistence.</param>
/// <param name="DisplayName">Name shown in the model picker.</param>
/// <param name="Capability">What this model does.</param>
/// <param name="Runtime">Which runtime executes it.</param>
/// <param name="RepoId">
/// Runtime-scoped model reference. For <see cref="LocalRuntime.TransformersJs"/> this is a
/// HuggingFace repo path; for <see cref="LocalRuntime.WebLlm"/> it is an MLC model-id <em>stem</em>
/// with no quantization suffix — the adapter appends the suffix when it interprets the active
/// variant. The registry never stores a quantization-bearing id: that would put dtype in two
/// places and let them drift.
/// </param>
/// <param name="VariantChain">
/// Ordered fallback chain, most-preferred first. Pruned against device capabilities before use.
/// </param>
/// <param name="ApproxDownloadMb">Rough download size of the preferred variant, shown in the UI.</param>
public sealed record LocalModelDescriptor(
    LocalModelId Id,
    string DisplayName,
    LocalCapability Capability,
    LocalRuntime Runtime,
    string RepoId,
    IReadOnlyList<DtypeVariant> VariantChain,
    int ApproxDownloadMb);
