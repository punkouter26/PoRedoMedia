using System.Net.Sockets;

namespace PoRedoMedia.E2EUI;

/// <summary>
/// A fact that needs the app running at <c>E2E_BASE_URL</c> (default http://localhost:4100).
/// When nothing is listening the test is reported as skipped, never as passed.
/// </summary>
public sealed class LiveServerFactAttribute : FactAttribute
{
    public static string BaseUrl { get; } =
        (Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:4100").TrimEnd('/');

    private static readonly Lazy<bool> Reachable = new(() =>
    {
        try
        {
            var uri = new Uri(BaseUrl);
            using var tcp = new TcpClient();
            return tcp.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            return false;
        }
    });

    public LiveServerFactAttribute()
    {
        if (!Reachable.Value)
            Skip = $"No app is running at {BaseUrl}.";
    }
}
