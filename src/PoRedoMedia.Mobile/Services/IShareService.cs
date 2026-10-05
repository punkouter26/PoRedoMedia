namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Service interface for native sharing and local saving of generated results.
/// </summary>
public interface IShareService
{
    /// <summary>
    /// Opens the native system share sheet with an image file.
    /// </summary>
    Task ShareImageAsync(byte[] imageBytes, string fileName, string title = "PoRedo Image");

    /// <summary>
    /// Opens the native system share sheet with any binary file (used for rendered Veo clips).
    /// </summary>
    Task ShareFileAsync(byte[] fileBytes, string fileName, string title = "PoRedo Clip");

    /// <summary>
    /// Opens the native system share sheet with text (e.g. rap roast lyrics).
    /// </summary>
    Task ShareTextAsync(string text, string title = "PoRedo Roast");

    /// <summary>
    /// Saves the media (image or video) to the device's public gallery (e.g. Pictures/PoRedoMedia or Movies/PoRedoMedia)
    /// embedding EXIF metadata where applicable.
    /// </summary>
    Task<string?> SaveToDeviceAsync(
        byte[] mediaBytes,
        string fileName,
        string contentType = "image/jpeg",
        Models.MediaMetadata? metadata = null);
}

