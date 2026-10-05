using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace PoRedoMedia.Mobile;

/// <summary>
/// Single activity. Beyond launching the app it serves two native entry points a browser page
/// can never see: Android share intents ("Share → PoRedo" from any app) and the "launch straight
/// into the camera" request carried by the quick-settings tile and the home-screen widget.
/// </summary>
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    new[] { Intent.ActionSend, Intent.ActionSendMultiple },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "image/*")]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>Intent extra set by the tile/widget to request a camera-first launch.</summary>
    public const string ExtraLaunchCamera = "launch_camera";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    private void HandleIntent(Intent? intent)
    {
        if (intent is null)
            return;

        if (intent.GetBooleanExtra(ExtraLaunchCamera, false))
            SharedImageInbox.RequestCameraLaunch();

        if (intent.Action != Intent.ActionSend && intent.Action != Intent.ActionSendMultiple)
            return;

        // Bundle.Get rather than GetParcelableExtra: the typed overload is API-33+ and the
        // untyped one is deprecated (a build error under TreatWarningsAsErrors). Bundle.Get is
        // neither — it returns the raw object, whatever shape the sending app chose.
        var raw = intent.Extras?.Get(Intent.ExtraStream);
        switch (raw)
        {
            case Android.Net.Uri single:
                StashUri(single);
                break;
            case System.Collections.IList many:
                foreach (var item in many.OfType<Android.Net.Uri>())
                    StashUri(item);
                break;
        }
    }

    private void StashUri(Android.Net.Uri uri)
    {
        try
        {
            using var stream = ContentResolver?.OpenInputStream(uri);
            if (stream is null)
                return;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            SharedImageInbox.Push(buffer.ToArray(), $"shared_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg");
        }
        catch (Exception)
        {
            // An unreadable share (permission revoked mid-flight, exotic provider) is skipped —
            // the sending app already showed success for the share itself, and a crash here
            // would take down the launch.
        }
    }
}

