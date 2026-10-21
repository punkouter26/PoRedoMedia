using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace PoRedoMedia.Api.Configuration;

public static class TelemetryExtensions
{
    /// <summary>
    /// Sends requests, dependencies, logs, and the app's own run traces and metrics to Application
    /// Insights. Off when no connection string is configured, which is every test and most local runs.
    /// </summary>
    public static void AddPoTelemetry(this WebApplicationBuilder builder)
    {
        var connectionString = builder.Configuration[ConfigKeys.ApplicationInsightsConnectionString]
            ?? builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString) || builder.Environment.IsEnvironment(PoEnvironments.Test))
            return;

        builder.Services.AddOpenTelemetry()
            .UseAzureMonitor(o => o.ConnectionString = connectionString)
            .WithTracing(t => t.AddSource(PoTelemetry.Name))
            .WithMetrics(m => m.AddMeter(PoTelemetry.Name));
    }
}
