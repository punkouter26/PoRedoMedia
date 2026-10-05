using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using PoRedoMedia.Mobile.Services;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Mobile.ViewModels;

/// <summary>One row of the gallery list — wraps the server DTO and lazily loads its bytes.</summary>
public partial class GalleryItemViewModel : ObservableObject
{
    public GalleryItemViewModel(MediaDto dto) => Dto = dto;

    public MediaDto Dto { get; }

    public string FileName => Dto.Title;

    public string KindLabel => Dto.Kind.ToString();

    public string CreatedLabel => Dto.CreatedAt.LocalDateTime.ToString("g");

    public string SizeLabel => Dto.SizeBytes switch
    {
        < 1024 => $"{Dto.SizeBytes} B",
        < 1024 * 1024 => $"{Dto.SizeBytes / 1024.0:F0} KB",
        _ => $"{Dto.SizeBytes / (1024.0 * 1024.0):F1} MB"
    };

    [ObservableProperty]
    private ImageSource? _image;

    [ObservableProperty]
    private bool _isLoaded;
}

/// <summary>
/// The user's persistent PoRedo gallery — the parity feature the phone never had. Every result
/// saved from any device (web included) shows up here, because it reads the same
/// <c>/api/user-images</c> collection the web gallery does.
/// </summary>
public partial class GalleryViewModel : ObservableObject
{
    private readonly MobileApiClient _apiClient;
    private readonly IBiometricGuard _biometric;
    private readonly IMobileSettingsService _settings;
    private readonly IShareService _shareService;

    public GalleryViewModel(
        MobileApiClient apiClient,
        IBiometricGuard biometric,
        IMobileSettingsService settings,
        IShareService shareService)
    {
        _apiClient = apiClient;
        _biometric = biometric;
        _settings = settings;
        _shareService = shareService;
    }

    public ObservableCollection<GalleryItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Pull to refresh";

    [ObservableProperty]
    private GalleryItemViewModel? _selected;

    [ObservableProperty]
    private bool _isLocked;

    /// <summary>
    /// Called on page show: engages the biometric gate when enabled in Settings, then refreshes
    /// only if the gallery is unlocked. A device without enrolled credentials is never locked out.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (IsLocked || !_settings.LockGalleryWithBiometrics)
        {
            await RefreshAsync();
            return;
        }

        if (!await _biometric.IsAvailableAsync())
        {
            // Setting is on but this device has no enrolled biometrics — show, don't strand.
            await RefreshAsync();
            return;
        }

        IsLocked = true;
        StatusText = "Locked";
    }

    /// <summary>Shows the platform biometric prompt; refreshes on success.</summary>
    [RelayCommand]
    public async Task UnlockAsync()
    {
        if (await _biometric.UnlockAsync("Unlock your PoRedo gallery"))
        {
            IsLocked = false;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (!await _apiClient.EnsureAuthenticatedAsync(_settings.GuestId))
            {
                StatusText = "Sign-in failed. For now the phone app can only sign in to a Development or Test server.";
                Items.Clear();
                return;
            }

            StatusText = "Loading gallery…";
            var images = await _apiClient.ListGalleryAsync();

            Items.Clear();
            foreach (var dto in images)
                Items.Add(new GalleryItemViewModel(dto));

            // Bytes must come through the authenticated endpoint — the blob is not public.
            foreach (var item in Items)
            {
                try
                {
                    var bytes = await _apiClient.GetBytesAsync(item.Dto.ThumbUrl);
                    item.Image = ImageSource.FromStream(() => new MemoryStream(bytes));
                    item.IsLoaded = true;
                }
                catch
                {
                    // One unreadable image must not sink the whole gallery; the row simply shows
                    // its placeholder and the failure is visible as the missing thumbnail.
                }
            }

            StatusText = Items.Count == 0
                ? "Gallery is empty — make something in the studio to see it here."
                : $"{Items.Count} item{(Items.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            StatusText = $"Gallery error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task OpenAsync(GalleryItemViewModel item)
    {
        try
        {
            var bytes = await _apiClient.GetBytesAsync(item.Dto.Url);
            if (item.Dto.Kind == MediaKind.Image)
            {
                item.Image = ImageSource.FromStream(() => new MemoryStream(bytes));
                item.IsLoaded = true;
                Selected = item;
                return;
            }

            // Clips and roast audio go to whatever player the phone has.
            var path = Path.Combine(FileSystem.CacheDirectory, $"poredo_{item.Dto.Id:N}{ExtensionFor(item.Dto.ContentType)}");
            await File.WriteAllBytesAsync(path, bytes);
            await Launcher.Default.OpenAsync(new OpenFileRequest(item.FileName, new ReadOnlyFile(path, item.Dto.ContentType)));
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open {item.FileName}: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task DeleteAsync(GalleryItemViewModel item)
    {
        try
        {
            await _apiClient.DeleteAsync(item.Dto.Id);
            Items.Remove(item);
            if (Selected == item)
                Selected = null;
            StatusText = $"{Items.Count} item{(Items.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SaveToDeviceAsync(GalleryItemViewModel? item)
    {
        var target = item ?? Selected;
        if (target is null) return;

        try
        {
            StatusText = $"Exporting {target.FileName} to device Photos…";
            var bytes = await _apiClient.GetBytesAsync(target.Dto.Url);
            var meta = new Models.MediaMetadata(
                Title: target.FileName,
                PromptOrDescription: $"PoRedo {target.KindLabel} from gallery",
                ModelOrStyle: target.KindLabel,
                CreatedAt: target.Dto.CreatedAt);

            var path = await _shareService.SaveToDeviceAsync(bytes, target.FileName, target.Dto.ContentType, meta);
            StatusText = path != null
                ? $"Saved to Photos ({path})!"
                : "Saved to device!";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "video/mp4" => ".mp4",
        "audio/mpeg" => ".mp3",
        "audio/wav" => ".wav",
        _ => string.Empty,
    };

    [RelayCommand]
    public void CloseDetail() => Selected = null;
}
