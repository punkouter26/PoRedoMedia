using PoRedoMedia.Mobile.Models;

namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Service interface for capturing photos from the device camera or choosing from the photo gallery.
/// </summary>
public interface ICameraService
{
    /// <summary>
    /// Whether the device has camera hardware and media capture support.
    /// </summary>
    bool IsCaptureSupported { get; }

    /// <summary>
    /// Launches the device camera, captures a photo, and returns the optimized image payload.
    /// </summary>
    Task<ImageCaptureResult?> CapturePhotoAsync(IProgress<string>? stage = null, CancellationToken ct = default);

    /// <summary>
    /// Opens the device photo library to select an existing picture.
    /// </summary>
    Task<ImageCaptureResult?> PickPhotoAsync(IProgress<string>? stage = null, CancellationToken ct = default);
}

