namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Persists app configuration in MAUI platform preferences.
/// </summary>
public class MobileSettingsService : IMobileSettingsService
{
    private const string ServerUrlKey = "poredo_server_url";
    private const string GuestIdKey = "poredo_guest_id";
    private const string StyleKey = "poredo_selected_style";
    private const string AutoSaveKey = "poredo_auto_save";
    private const string BiometricLockKey = "poredo_biometric_lock";

    public string ServerUrl
    {
        get => Preferences.Default.Get(ServerUrlKey, DefaultServerUrl);
        set => Preferences.Default.Set(ServerUrlKey, string.IsNullOrWhiteSpace(value) ? DefaultServerUrl : value.Trim().TrimEnd('/'));
    }

    public string GuestId
    {
        get
        {
            var id = Preferences.Default.Get(GuestIdKey, string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                id = $"GUEST{Random.Shared.Next(10000000, 99999999)}";
                Preferences.Default.Set(GuestIdKey, id);
            }
            return id;
        }
        set => Preferences.Default.Set(GuestIdKey, value);
    }

    public string SelectedStyle
    {
        get => Preferences.Default.Get(StyleKey, "Cyberpunk");
        set => Preferences.Default.Set(StyleKey, value);
    }

    public bool AutoSaveToGallery
    {
        get => Preferences.Default.Get(AutoSaveKey, false);
        set => Preferences.Default.Set(AutoSaveKey, value);
    }

    public bool LockGalleryWithBiometrics
    {
        get => Preferences.Default.Get(BiometricLockKey, false);
        set => Preferences.Default.Set(BiometricLockKey, value);
    }

    public Uri GetBaseUri()
    {
        var raw = ServerUrl;
        if (!raw.EndsWith('/'))
            raw += "/";

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return uri;

        return new Uri(DefaultServerUrl + "/");
    }

    // ponytail: the phone's own loopback, reached through `adb reverse` (scripts/run-mobile.ps1).
    // Upload and download links from a local server point at 127.0.0.1 as well, so this is the one
    // address where both the app and its storage resolve, on an emulator and on a USB phone alike.
    // Change the default to the deployed address once the app can sign in to a deployed server.
    private const string DefaultServerUrl = "http://127.0.0.1:4100";
}
