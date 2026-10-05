using Android;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Service.QuickSettings;

namespace PoRedoMedia.Mobile.Platforms.Android;

/// <summary>
/// Quick-settings tile: "Snap and Roast" launches the app straight into a camera capture, so the
/// photo-to-meme loop starts from the notification shade without hunting for the icon. Browser
/// pages cannot host a system tile — this is a native-only entry point.
/// </summary>
[Service(
    Name = "com.poredomedia.mobile.tile",
    Permission = global::Android.Manifest.Permission.BindQuickSettingsTile,
    Label = "Snap and Roast",
    Icon = "@android:drawable/ic_menu_camera",
    Exported = true)]
[IntentFilter(new[] { TileService.ActionQsTile })]
public sealed class SnapRoastTileService : TileService
{
    public override void OnClick()
    {
        var launch = new Intent(this, typeof(MainActivity));
        launch.PutExtra(MainActivity.ExtraLaunchCamera, true);
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);

        var pending = PendingIntent.GetActivity(
            this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        // The PendingIntent overload is deprecated on API 34+ in favour of one that also takes
        // launch options; the options variant needs a display handle the tile does not always
        // have at click time. Suppressed deliberately rather than branching on a nullable.
#pragma warning disable CS0618
#pragma warning disable CA1416 // StartActivityAndCollapse(PendingIntent) is API 34+
#pragma warning disable CS8604 // PendingIntent.GetActivity is not annotated nullable
        StartActivityAndCollapse(pending);
#pragma warning restore CS8604
#pragma warning restore CA1416
#pragma warning restore CS0618
    }
}
