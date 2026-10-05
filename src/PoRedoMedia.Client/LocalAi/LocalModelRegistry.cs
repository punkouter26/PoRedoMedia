namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// The single catalog of browser-executable models.
/// </summary>
/// <remarks>
/// Both runtimes read from here. An entry's <see cref="LocalModelDescriptor.VariantChain"/> is the
/// authoritative fallback order; adapters never invent one.
/// </remarks>
public static class LocalModelRegistry
{
    /// <summary>
    /// Every registered model.
    /// </summary>
    /// <remarks>
    /// Each chain ends in a variant that survives pruning on the weakest supported device — without
    /// that, <see cref="DtypeChain.Create"/> throws on a machine with no WebGPU, which would make
    /// the model unusable rather than merely slow.
    /// </remarks>
    public static IReadOnlyList<LocalModelDescriptor> All { get; } =
    [
        new LocalModelDescriptor(
            Id: new LocalModelId("florence2-base"),
            DisplayName: "Florence-2 base",
            Capability: LocalCapability.Vision,
            Runtime: LocalRuntime.TransformersJs,
            RepoId: "onnx-community/Florence-2-base",
            VariantChain: [DtypeVariant.Q4, DtypeVariant.F16, DtypeVariant.F32],
            ApproxDownloadMb: 230),

        new LocalModelDescriptor(
            Id: new LocalModelId("qwen2.5-0.5b-instruct"),
            DisplayName: "Qwen2.5 0.5B Instruct",
            Capability: LocalCapability.Text,
            Runtime: LocalRuntime.WebLlm,
            RepoId: "Qwen2.5-0.5B-Instruct",
            VariantChain: [DtypeVariant.Q4F16, DtypeVariant.Q4],
            ApproxDownloadMb: 350),
    ];

    /// <summary>Looks up a descriptor by id.</summary>
    public static bool TryGet(LocalModelId id, out LocalModelDescriptor descriptor)
    {
        descriptor = All.FirstOrDefault(m => m.Id == id)!;
        return descriptor is not null;
    }

    /// <summary>Models providing a given capability.</summary>
    public static IReadOnlyList<LocalModelDescriptor> ByCapability(LocalCapability capability) =>
        [.. All.Where(m => m.Capability == capability)];

    /// <summary>
    /// The default model for a capability — the first registered, which is the preferred one.
    /// </summary>
    public static LocalModelDescriptor? DefaultFor(LocalCapability capability) =>
        All.FirstOrDefault(m => m.Capability == capability);

    /// <summary>
    /// Resolves the runtime-specific model reference for an active variant.
    /// </summary>
    /// <remarks>
    /// This is the entire difference between the two runtimes. transformers.js takes the repo path
    /// unchanged and receives the variant separately as a <c>dtype</c> option; WebLLM has no such
    /// option, so the variant is encoded into the model id itself. Keeping the translation here
    /// means the chain logic never has to know which runtime it is feeding.
    /// </remarks>
    public static string ResolveModelReference(LocalModelDescriptor descriptor, DtypeVariant variant)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return descriptor.Runtime switch
        {
            LocalRuntime.TransformersJs => descriptor.RepoId,
            LocalRuntime.WebLlm => $"{descriptor.RepoId}-{WebLlmSuffix(variant)}-MLC",
            _ => descriptor.RepoId,
        };
    }

    /// <summary>Maps a variant to WebLLM's quantization suffix.</summary>
    private static string WebLlmSuffix(DtypeVariant variant) => variant switch
    {
        DtypeVariant.Q4F16 => "q4f16_1",
        DtypeVariant.Q4 => "q4f32_1",
        DtypeVariant.F16 => "q4f16_1",
        DtypeVariant.F32 => "q4f32_1",
        _ => "q4f32_1",
    };

    /// <summary>Maps a variant to the transformers.js <c>dtype</c> string.</summary>
    public static string TransformersDtype(DtypeVariant variant) => variant switch
    {
        DtypeVariant.Q4 => "q4",
        DtypeVariant.Q4F16 => "q4f16",
        DtypeVariant.F16 => "fp16",
        DtypeVariant.F32 => "fp32",
        _ => "q4",
    };
}
