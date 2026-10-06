using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Api.Features.Media;

/// <summary>What may be uploaded, decided before an upload link is issued.</summary>
public static class UploadValidation
{
    public const long MaxImageBytes = 10L * 1024 * 1024;
    public const long MaxVideoBytes = 200L * 1024 * 1024;
    public const double MaxVideoSeconds = 60;

    /// <summary>
    /// How far past the limit a video may measure and still be taken. A clip recorded to "one
    /// minute" rarely measures exactly 60 seconds: recorders finish the frame they are on.
    /// </summary>
    public const double VideoSecondsSlack = 2;

    /// <summary>Width times height. Decoding costs memory per pixel, whatever the file size.</summary>
    public const long MaxImagePixels = 40_000_000;

    private static readonly Dictionary<string, (MediaKind Kind, string ContentType)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = (MediaKind.Image, "image/jpeg"),
        [".jpeg"] = (MediaKind.Image, "image/jpeg"),
        [".png"] = (MediaKind.Image, "image/png"),
        [".webp"] = (MediaKind.Image, "image/webp"),
        [".gif"] = (MediaKind.Image, "image/gif"),
        [".mp4"] = (MediaKind.Video, "video/mp4"),
        [".mov"] = (MediaKind.Video, "video/quicktime"),
        [".webm"] = (MediaKind.Video, "video/webm"),
    };

    public readonly record struct Result(MediaKind Kind, string Extension, string ContentType, string? Error);

    public static long MaxBytes(MediaKind kind) => kind == MediaKind.Video ? MaxVideoBytes : MaxImageBytes;

    public static Result Classify(string? fileName, long sizeBytes)
    {
        fileName ??= string.Empty;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!Types.TryGetValue(extension, out var type))
            return Refuse("That file type is not supported. Use JPG, PNG, WEBP, GIF, MP4, MOV or WEBM.");
        if (sizeBytes <= 0)
            return Refuse("The file is empty.");
        if (type.Kind == MediaKind.Image && sizeBytes > MaxImageBytes)
            return Refuse("Images can be up to 10 MB.");
        if (type.Kind == MediaKind.Video && sizeBytes > MaxVideoBytes)
            return Refuse("Videos can be up to 200 MB.");

        return new(type.Kind, extension == ".jpeg" ? ".jpg" : extension, type.ContentType, null);
    }

    private static Result Refuse(string reason) => new(default, "", "", reason);
}
