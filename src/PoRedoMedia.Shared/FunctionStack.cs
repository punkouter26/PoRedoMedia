using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Shared;

/// <summary>
/// Which functions may be applied together. Shared by the Create page, which disables what
/// cannot be added, and the server, which refuses an invalid stack.
/// </summary>
public static class FunctionStack
{
    public static MediaKind KindOf(MediaFunction function) =>
        function is MediaFunction.Memeify or MediaFunction.VideoRoast or MediaFunction.Captions
            ? MediaKind.Video
            : MediaKind.Image;

    /// <summary>The functions offered for a media kind, in run order. Audio has none.</summary>
    public static IReadOnlyList<MediaFunction> For(MediaKind kind) =>
        kind == MediaKind.Audio ? [] : [.. Enum.GetValues<MediaFunction>().Where(f => KindOf(f) == kind)];

    /// <summary>Null when the stack may run on that kind of media; otherwise why not.</summary>
    public static string? Validate(MediaKind kind, IReadOnlyCollection<MediaFunction> functions)
    {
        if (functions.Count == 0)
            return "Pick at least one function.";
        if (functions.Distinct().Count() != functions.Count)
            return "A function can only be picked once.";
        var wrong = functions.Where(f => kind == MediaKind.Audio || KindOf(f) != kind).ToArray();
        if (wrong.Length > 0)
            return $"{Label(wrong[0])} cannot be applied to {kind.ToString().ToLowerInvariant()}.";
        if (functions.Contains(MediaFunction.BulkStyles) && functions.Count > 1)
            return "Bulk styles runs on its own.";
        return null;
    }

    public static bool CanAdd(IReadOnlyCollection<MediaFunction> picked, MediaFunction function) =>
        Validate(KindOf(function), [.. picked, function]) is null;

    /// <summary>The fixed order steps run in, whatever order they were picked in.</summary>
    public static IReadOnlyList<MediaFunction> InRunOrder(IEnumerable<MediaFunction> functions) => [.. functions.Order()];

    public static string Label(MediaFunction function) => function switch
    {
        MediaFunction.Restyle => "Restyle",
        MediaFunction.MemeCaption => "Meme caption",
        MediaFunction.RapRoast => "Rap roast",
        MediaFunction.PhotoToVideo => "Photo to video",
        MediaFunction.BulkStyles => "Bulk styles",
        MediaFunction.Memeify => "Meme-ify",
        MediaFunction.VideoRoast => "Insult roast",
        MediaFunction.Captions => "Auto-captions",
        _ => function.ToString(),
    };
}
