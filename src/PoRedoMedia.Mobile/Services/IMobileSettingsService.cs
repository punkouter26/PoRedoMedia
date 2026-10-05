namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Manages mobile client configuration and persistent preferences.
/// </summary>
public interface IMobileSettingsService
{
    string ServerUrl { get; set; }
    string GuestId { get; set; }
    string SelectedStyle { get; set; }
    bool AutoSaveToGallery { get; set; }




    /// <summary>
    /// Gate the gallery behind device biometrics. Only takes effect when the device has
    /// enrolled hardware credentials — the lock never strands a user without them.
    /// </summary>
    bool LockGalleryWithBiometrics { get; set; }

    /// <summary>
    /// Returns the resolved API base URI with trailing slash guaranteed.
    /// </summary>
    Uri GetBaseUri();
}

