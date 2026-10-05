using PoRedoMedia.Mobile.Models;

namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Native MAUI implementation of camera capture and photo gallery picking.
/// </summary>
public class MauiCameraService : ICameraService
{
    private readonly IImageOptimizationService _optimizer;

    public MauiCameraService(IImageOptimizationService optimizer)
    {
        _optimizer = optimizer;
    }

    public bool IsCaptureSupported => MediaPicker.Default.IsCaptureSupported;

    public async Task<ImageCaptureResult?> CapturePhotoAsync(IProgress<string>? stage = null, CancellationToken ct = default)
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Camera>();
                if (status != PermissionStatus.Granted)
                {
                    Console.WriteLine($"[MauiCameraService] Camera permission denied: {status}");
                    return null;
                }
            }

            var photo = await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions
            {
                Title = "Capture Image for PoRedo"
            });

            if (photo == null)
            {
                Console.WriteLine("[MauiCameraService] User cancelled camera capture.");
                return null;
            }

            stage?.Report("Optimizing photo…");


            await using var stream = await photo.OpenReadAsync();
            var fileName = photo.FileName ?? $"camera_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg";
            var contentType = photo.ContentType ?? "image/jpeg";

            return await _optimizer.OptimizeAsync(stream, fileName, contentType, maxDimension: 1280, quality: 85, ct: ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiCameraService] Error during CapturePhotoAsync: {ex}");
            return null;
        }
    }

    public async Task<ImageCaptureResult?> PickPhotoAsync(IProgress<string>? stage = null, CancellationToken ct = default)
    {
        try
        {
            var photos = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions
            {
                Title = "Select Image for PoRedo"
            });

            var photo = photos?.FirstOrDefault();
            if (photo == null)
            {
                Console.WriteLine("[MauiCameraService] User cancelled photo picking.");
                return null;
            }

            stage?.Report("Optimizing photo…");


            await using var stream = await photo.OpenReadAsync();
            var fileName = photo.FileName ?? $"gallery_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg";
            var contentType = photo.ContentType ?? "image/jpeg";

            return await _optimizer.OptimizeAsync(stream, fileName, contentType, maxDimension: 1280, quality: 85, ct: ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiCameraService] Error during PickPhotoAsync: {ex}");
            return null;
        }
    }
}
