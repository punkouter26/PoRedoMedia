using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace PoRedoMedia.Mobile.Services;

#pragma warning disable CS8602 // Context is annotated nullable in the Android binding assembly;
                             // every call site already null-guards, the warnings are noise.

/// <summary>
/// Foreground service that carries a "rendering your clip…" notification for the life of a Veo
/// render. Without it, Android freezes the app the moment the user locks the phone — and a
/// 1–5 minute render dies silently in the background.
/// </summary>
[Service(Name = "com.poredomedia.mobile.render", Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class RenderForegroundService : Service
{
    public const string ChannelId = "poredo_render";
    public const int ForegroundNotificationId = 42;
    public const int CompletionNotificationId = 43;

    public static void EnsureChannel(Context context)
    {
        var manager = (NotificationManager?)context!.GetSystemService(NotificationService);
        if (manager is null || manager.GetNotificationChannel(ChannelId) is not null)
            return;

#pragma warning disable CA1416 // NotificationChannel is API 26+; SupportedOSPlatformVersion=24
        manager.CreateNotificationChannel(new NotificationChannel(
            ChannelId,
            "Clip renders",
            NotificationImportance.Low)
        {
            Description = "Progress and completion of image-to-video renders."
        });
#pragma warning restore CA1416
    }

    public static Notification BuildNotification(Context context, string text, bool ongoing)
    {
        EnsureChannel(context!);
        var builder = new Notification.Builder(context!, ChannelId)
            .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)
            .SetContentTitle("PoRedo clip")
            .SetContentText(text)
            .SetOngoing(ongoing);

        if (ongoing)
            builder.SetProgress(0, 0, true);

        return builder.Build();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification(this, "Rendering your clip — you can lock the phone.", ongoing: true);
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(ForegroundNotificationId, notification, ForegroundService.TypeDataSync);
        else
            StartForeground(ForegroundNotificationId, notification);
        return StartCommandResult.NotSticky;
    }
}

/// <summary>Android implementation: foreground promotion + completion notification.</summary>
public sealed class AndroidRenderMonitor : IRenderMonitorService
{
    public async Task StartAsync(string title)
    {
        var context = Platform.AppContext;
        await RequestNotificationPermissionAsync();

        RenderForegroundService.EnsureChannel(context);
        var intent = new Intent(context, typeof(RenderForegroundService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public Task CompleteAsync(string message, bool success)
    {
        var context = Platform.AppContext;
        if (context is null)
            return Task.CompletedTask;

        context.StopService(new Intent(context, typeof(RenderForegroundService)));

        var notification = RenderForegroundService.BuildNotification(context, message, ongoing: false);
        NotificationManagerCompat.From(context).Notify(RenderForegroundService.CompletionNotificationId, notification);
        return Task.CompletedTask;
    }

    private static async Task RequestNotificationPermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            return;

        var context = Platform.AppContext;
        if (AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                context, Android.Manifest.Permission.PostNotifications) == Permission.Granted)
            return;

        var activity = Platform.CurrentActivity;
        if (activity is null)
            return;

        await Permissions.RequestAsync<Permissions.PostNotifications>();
    }
}
