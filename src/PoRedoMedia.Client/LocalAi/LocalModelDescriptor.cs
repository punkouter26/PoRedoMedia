namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// Quantization variant, ordered from smallest/fastest to largest/most accurate.
/// </summary>
public enum DtypeVariant
{
    /// <summary>4-bit. Smallest download, widest device support.</summary>
    Q4 = 0,

    /// <summary>Half precision. Requires the <c>shader-f16</c> GPU feature.</summary>
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
/// A browser-executable model, run by transformers.js.
/// </summary>
/// <param name="Id">Stable internal id, used in logs.</param>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="RepoId">HuggingFace repo path.</param>
/// <param name="VariantChain">
/// Ordered fallback chain, most-preferred first. Pruned against device capabilities before use.
/// </param>
/// <param name="ApproxDownloadMb">Rough download size of the preferred variant, shown in the UI.</param>
public sealed record LocalModelDescriptor(
    string Id,
    string DisplayName,
    string RepoId,
    IReadOnlyList<DtypeVariant> VariantChain,
    int ApproxDownloadMb);
