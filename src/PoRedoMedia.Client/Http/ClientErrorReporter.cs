using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Http;

/// <summary>
/// Sends the browser's errors to the server, where they join the server's own logs. A component
/// that throws is logged by Blazor at Critical, so this is how such a failure gets noticed.
/// </summary>
/// <remarks>At most <see cref="MaxReports"/> per page load, so a render loop cannot flood the logs.</remarks>
public sealed class ClientErrorReporter(Uri baseAddress) : ILoggerProvider
{
    private const int MaxReports = 10;

    private readonly HttpClient _http = new(new AntiforgeryTokenHandler(() => new HttpClient { BaseAddress = baseAddress })
    {
        InnerHandler = new HttpClientHandler(),
    })
    { BaseAddress = baseAddress };

    private int _sent;

    /// <summary>The address the user is on. Set once navigation is available.</summary>
    public Func<string?> Page { get; set; } = () => null;

    public ILogger CreateLogger(string categoryName) => new Reporter(this, categoryName);

    public void Dispose() => _http.Dispose();

    private void Report(string category, string message, Exception? exception)
    {
        if (Interlocked.Increment(ref _sent) > MaxReports)
            return;

        // Fire and forget: reporting must never become a second failure on the user's screen.
        _ = SendAsync(new ClientErrorDto(message, exception?.ToString(), category, Page()));
    }

    private async Task SendAsync(ClientErrorDto error)
    {
        try
        {
            using var _ = await _http.PostAsJsonAsync("api/client-errors", error, WireJson.Default.ClientErrorDto);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Offline or signed out: the console still has it.
        }
    }

    private sealed class Reporter(ClientErrorReporter owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // A failure while sending a report is not reported again.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error && !category.StartsWith("System.Net.Http", StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                owner.Report(category, formatter(state, exception), exception);
        }
    }
}
