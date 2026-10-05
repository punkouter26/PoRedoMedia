using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoRedoMedia.Mobile.Services;

namespace PoRedoMedia.Mobile.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IMobileSettingsService _settings;
    private readonly MobileApiClient _apiClient;

    [ObservableProperty]
    private string _serverUrl;

    [ObservableProperty]
    private string _selectedStyle;

    [ObservableProperty]
    private bool _autoSaveToGallery;

    [ObservableProperty]
    private string _connectionStatus = "Not tested";

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _lockGalleryWithBiometrics;

    public string[] AvailableStyles { get; } =
    [
        "Cyberpunk",
        "Anime / Studio Ghibli",
        "Oil Painting Masterpiece",
        "Cinematic 3D Render",
        "Watercolor Sketch",
        "Pixel Art 16-bit",
        "Retro Vintage 70s"
    ];

    public SettingsViewModel(
        IMobileSettingsService settings,
        MobileApiClient apiClient)
    {
        _settings = settings;
        _apiClient = apiClient;
        _serverUrl = _settings.ServerUrl;
        _selectedStyle = _settings.SelectedStyle;
        _autoSaveToGallery = _settings.AutoSaveToGallery;
        _lockGalleryWithBiometrics = _settings.LockGalleryWithBiometrics;
    }

    /// <summary>Display names of every catalogued on-device model, biggest last.</summary>
    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        IsTesting = true;
        ConnectionStatus = "Connecting…";
        _settings.ServerUrl = ServerUrl;

        try
        {
            var isAlive = await _apiClient.PingAsync();
            if (isAlive)
            {
                ConnectionStatus = "✅ Connected to backend successfully!";
            }
            else
            {
                ConnectionStatus = "❌ Server unreachable. Verify your IP/port and ensure backend is running.";
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    public void SaveSettings()
    {
        _settings.ServerUrl = ServerUrl;
        _settings.SelectedStyle = SelectedStyle;
        _settings.AutoSaveToGallery = AutoSaveToGallery;
        _settings.LockGalleryWithBiometrics = LockGalleryWithBiometrics;
    }
}
