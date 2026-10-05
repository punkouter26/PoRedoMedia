using PoRedoMedia.Mobile.Services;
using PoRedoMedia.Mobile.ViewModels;
using PoRedoMedia.Mobile.Views;

namespace PoRedoMedia.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();

        // ── Core Mobile Services ──────────────────────────────
        builder.Services.AddSingleton<IImageOptimizationService, ImageOptimizationService>();
        builder.Services.AddSingleton<ICameraService, MauiCameraService>();
        builder.Services.AddSingleton<IMobileSettingsService, MobileSettingsService>();
        builder.Services.AddSingleton(sp => new MobileApiClient(sp.GetRequiredService<IMobileSettingsService>().GetBaseUri));
        builder.Services.AddSingleton<IShareService, MauiShareService>();

        // Native entry points a browser page cannot have: share-target photos in, render
        // notifications out. Android gets the real thing; elsewhere a documented no-op.
#if ANDROID
        builder.Services.AddSingleton<IRenderMonitorService, AndroidRenderMonitor>();
        builder.Services.AddSingleton<ISharedImageInbox, Platforms.Android.AndroidSharedImageInbox>();
        builder.Services.AddSingleton<IBiometricGuard, Platforms.Android.BiometricGuard>();
        builder.Services.AddSingleton<IAudioPlayerService, Platforms.Android.AndroidAudioPlayer>();
#else
        builder.Services.AddSingleton<IRenderMonitorService, NullRenderMonitorService>();
        builder.Services.AddSingleton<ISharedImageInbox, NullSharedImageInbox>();
        builder.Services.AddSingleton<IBiometricGuard, NullBiometricGuard>();
        builder.Services.AddSingleton<IAudioPlayerService, NullAudioPlayer>();
#endif

        // ── ViewModels ────────────────────────────────────────
        builder.Services.AddTransient<MainViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<GalleryViewModel>();

        // ── Views / Pages ─────────────────────────────────────
        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<GalleryPage>();
        // CameraX pro-capture (#9) was deferred: the AndroidX binding API surface on CameraX 1.4
        // did not line up with the docs we developed against. The Pro Shot button on MainPage
        // falls back to MediaPicker, which still meets the user goal even though it does not
        // expose torch / tap-to-focus. See SPEC.md §15.

        return builder.Build();
    }
}

