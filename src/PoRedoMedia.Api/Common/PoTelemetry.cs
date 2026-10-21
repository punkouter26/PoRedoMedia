using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// The app's own traces and metrics: one span per run with one child per step, and how long
/// renders take. Exported to Application Insights when a connection string is configured;
/// otherwise nothing listens and they cost nothing.
/// </summary>
public static class PoTelemetry
{
    public const string Name = "PoRedoMedia";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    /// <summary>Finished runs, tagged with their outcome and functions.</summary>
    public static readonly Counter<long> RunsFinished = Meter.CreateCounter<long>("poredomedia.runs.finished", description: "Runs that ended");

    public static readonly Histogram<double> StepSeconds = Meter.CreateHistogram<double>("poredomedia.step.duration", "s", "How long one run step took");

    public static readonly Histogram<double> FFmpegSeconds = Meter.CreateHistogram<double>("poredomedia.ffmpeg.duration", "s", "How long one ffmpeg job took");

    /// <summary>Runs picked up again after a restart had cut them short.</summary>
    public static readonly Counter<long> RunsResumed = Meter.CreateCounter<long>("poredomedia.runs.resumed", description: "Runs resumed after a restart");

    /// <summary>Errors the browser reported.</summary>
    public static readonly Counter<long> ClientErrors = Meter.CreateCounter<long>("poredomedia.client.errors", description: "Errors reported by the browser");
}
