using PoRedoMedia.Mobile.Models;

namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Prepares and optimizes high-resolution phone camera images for fast AI processing over mobile networks.
/// </summary>
public interface IImageOptimizationService
{
    /// <summary>
    /// Reads and optimizes an image from a stream (e.g. from camera capture) into a streamlined AI-ready payload.
    /// </summary>
    Task<ImageCaptureResult> OptimizeAsync(
        Stream imageStream,
        string fileName,
        string contentType,
        int maxDimension = 1280,
        int quality = 85,
        CancellationToken ct = default);

    /// <summary>
    /// Optimizes raw byte data.
    /// </summary>
    Task<ImageCaptureResult> OptimizeAsync(
        byte[] rawBytes,
        string fileName,
        string contentType,
        int maxDimension = 1280,
        int quality = 85,
        CancellationToken ct = default);
}

