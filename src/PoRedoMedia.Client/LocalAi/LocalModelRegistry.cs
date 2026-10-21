namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// The browser-executable models. There is one: the picture describer.
/// </summary>
public static class LocalModelRegistry
{
    /// <summary>
    /// Florence-2, which describes pictures. Its chain ends in a variant that survives pruning on
    /// the weakest supported device; without that, <see cref="DtypeChain.Create"/> throws on a
    /// machine with no WebGPU, which would make the model unusable rather than merely slow.
    /// </summary>
    public static LocalModelDescriptor Vision { get; } = new(
        Id: "florence2-base",
        DisplayName: "Florence-2 base",
        RepoId: "onnx-community/Florence-2-base",
        VariantChain: [DtypeVariant.Q4, DtypeVariant.F16, DtypeVariant.F32],
        ApproxDownloadMb: 230);

    /// <summary>Maps a variant to the transformers.js <c>dtype</c> string.</summary>
    public static string TransformersDtype(DtypeVariant variant) => variant switch
    {
        DtypeVariant.F16 => "fp16",
        DtypeVariant.F32 => "fp32",
        _ => "q4",
    };
}
