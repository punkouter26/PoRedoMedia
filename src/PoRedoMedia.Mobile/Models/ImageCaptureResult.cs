namespace PoRedoMedia.Mobile.Models;

/// <summary>
/// Immutable record representing a captured or selected photo prepared for processing.
/// </summary>
public record ImageCaptureResult(
    string FileName,
    string ContentType,
    byte[] Bytes,
    string Base64Data,
    long FileSizeBytes,
    int? Width = null,
    int? Height = null)
{
    /// <summary>
    /// Formatted human-readable file size (e.g. "320 KB").
    /// </summary>
    public string FormattedSize => FileSizeBytes switch
    {
        < 1024 => $"{FileSizeBytes} B",
        < 1024 * 1024 => $"{FileSizeBytes / 1024.0:F1} KB",
        _ => $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB"
    };

    /// <summary>
    /// Formatted dimensions or size summary.
    /// </summary>
    public string FormattedSummary => Width.HasValue && Height.HasValue
        ? $"{Width}×{Height} px ({FormattedSize})"
        : FormattedSize;
}

