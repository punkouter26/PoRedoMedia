using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Platform-agnostic handle the ViewModel uses to show a render notification. No-op off Android.
/// </summary>
public interface IRenderMonitorService
{
    /// <summary>
    /// Shows the persistent "rendering…" notification and promotes the process to foreground so
    /// the poll loop survives the user locking the phone or switching apps mid-render.
    /// </summary>
    Task StartAsync(string title);

    /// <summary>
    /// Drops the foreground promotion and posts the completion notification.
    /// </summary>
    Task CompleteAsync(string message, bool success);
}

/// <summary>The no-op used on platforms without the Android implementation.</summary>
public sealed class NullRenderMonitorService : IRenderMonitorService
{
    public Task StartAsync(string title) => Task.CompletedTask;
    public Task CompleteAsync(string message, bool success) => Task.CompletedTask;
}
