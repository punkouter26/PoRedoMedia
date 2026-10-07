using System.Diagnostics;
using System.Text;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// Runs ffmpeg and ffprobe: finds the binaries (configured path, the bundled copy in the publish
/// output, or PATH), enforces the render time limit, and turns stderr into logs.
/// </summary>
public sealed partial class FFmpegProcess
{
    /// <summary>Inputs can be signed storage links, and ffmpeg echoes them. The signature never reaches a log.</summary>
    internal static string WithoutSignatures(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"([?&]sig=)[^&\s""']+", "$1***");

    [LoggerMessage(Level = LogLevel.Warning, Message = "ffprobe failed or timed out ({Arguments})")]
    private partial void LogProbeFailed(Exception ex, string arguments);

    /// <summary>Wall-clock ceiling for a single ffmpeg render before it is aborted as failed.</summary>
    private const int RenderTimeoutMinutes = 5;

    /// <summary>Wall-clock ceiling for one ffprobe call. It reads container headers; a healthy one takes milliseconds.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<FFmpegProcess> _logger;
    private readonly string? _binPath;

    public FFmpegProcess(IConfiguration configuration, ILogger<FFmpegProcess> logger)
    {
        _logger = logger;
        // An empty setting counts as unset, so it falls through to the bundled copy and then PATH.
        _binPath = configuration["FFmpegBinPath"] is { Length: > 0 } configured ? configured : ResolveBundledBinPath();
        if (!string.IsNullOrWhiteSpace(_binPath))
        {
            _logger.LogInformation("ffmpeg: using FFmpegBinPath = {Path}", _binPath);
            EnsureExecutable(_binPath);
        }
    }

    public Task<int> RunAsync(string args, MediaId mediaId, CancellationToken cancellationToken) =>
        RunAsync(args, mediaId.ToString(), cancellationToken);

    public async Task<int> RunAsync(string args, string jobLabel, CancellationToken cancellationToken)
    {
        var psi = BuildPsi("ffmpeg", args);
        psi.RedirectStandardError = true;

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
                _logger.LogTrace("[FFmpeg:{Job}] {Line}", jobLabel, e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        // Hard timeout so a pathologically slow or stuck encode fails cleanly instead of
        // leaving the run stuck in progress forever.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(RenderTimeoutMinutes));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);

            // Distinguish our render timeout from a genuine host-shutdown cancellation: the former
            // is a real failure the user should see; the latter is an expected teardown.
            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("FFmpeg render exceeded {Minutes}-minute limit for {Job}; aborted.\n{Stderr}",
                    RenderTimeoutMinutes, jobLabel, WithoutSignatures(stderr.ToString()));
                throw new TimeoutException(
                    $"Render exceeded the {RenderTimeoutMinutes}-minute limit and was aborted. " +
                    "The source video may be too large/high-resolution for the current host.");
            }

            throw;
        }

        if (process.ExitCode != 0)
            _logger.LogError("FFmpeg stderr for {Job}:\n{Stderr}", jobLabel, stderr);

        return process.ExitCode;
    }

    /// <summary>
    /// Returns true when the file has at least one audio stream (ffprobe). A failed probe reads
    /// as "no audio", so the render still succeeds with the added sounds only.
    /// </summary>
    public async Task<bool> HasAudioStreamAsync(string path, CancellationToken ct)
        => !string.IsNullOrWhiteSpace(await ProbeAsync(
            $"-v error -select_streams a -show_entries stream=index -of csv=p=0 \"{path}\"", ct));

    /// <summary>The container's duration in seconds, or 0 when ffprobe could not tell.</summary>
    public async Task<double> DurationSecondsAsync(string path, CancellationToken ct)
        => double.TryParse(
               (await ProbeAsync($"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"", ct))?.Trim(),
               System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture,
               out var duration) && duration > 0
            ? duration
            : 0;

    /// <summary>
    /// ffprobe's stdout, or null when it could not start, failed or ran past
    /// <see cref="ProbeTimeout"/> (it is then killed, like a render that overruns). A probe had no
    /// limit at all, so one stuck on a damaged upload held the run's lane for good.
    /// </summary>
    private async Task<string?> ProbeAsync(string args, CancellationToken ct)
    {
        try
        {
            using var probe = Process.Start(BuildPsi("ffprobe", args));
            if (probe is null)
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                var output = await probe.StandardOutput.ReadToEndAsync(timeout.Token);
                await probe.WaitForExitAsync(timeout.Token);
                return output;
            }
            catch (OperationCanceledException)
            {
                probe.Kill(entireProcessTree: true);
                throw;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogProbeFailed(ex, WithoutSignatures(args));
            return null;
        }
    }

    /// <summary>
    /// Locates the static ffmpeg/ffprobe bundled into the publish output under <c>ffmpeg/</c>.
    /// The ZIP deploy ships those binaries precisely so the app does not need a container image
    /// with ffmpeg installed system-wide. Returns null when the directory is absent (dev machines and the Docker image both
    /// have ffmpeg on PATH), leaving the bare-name lookup in <see cref="BuildPsi"/> in charge.
    /// </summary>
    private static string? ResolveBundledBinPath()
    {
        var binDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        var exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return File.Exists(Path.Combine(binDir, exeName)) ? binDir : null;
    }

    /// <summary>
    /// The font the deploy workflow bundles beside the app, for hosts with no fonts installed
    /// (the App Service .NET image). Absent on dev machines, where a system font is found first.
    /// </summary>
    internal static string BundledFontPath { get; } = Path.Combine(AppContext.BaseDirectory, "fonts", "DejaVuSans-Bold.ttf");

    /// <summary>
    /// Restores the Unix executable bit on the bundled binaries. ZIP deployment is not a reliable
    /// carrier for file modes — Kudu's extraction can drop them — and a non-executable ffmpeg
    /// surfaces only at Process.Start as a bare "Permission denied" with no hint at the cause.
    /// Idempotent, and a no-op when the package arrived with its modes intact.
    /// </summary>
    private void EnsureExecutable(string binDir)
    {
        if (OperatingSystem.IsWindows())
            return;

        const UnixFileMode executeBits =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        foreach (var exeName in new[] { "ffmpeg", "ffprobe" })
        {
            var path = Path.Combine(binDir, exeName);
            if (!File.Exists(path))
                continue;

            try
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & executeBits) != executeBits)
                    File.SetUnixFileMode(path, mode | executeBits);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read-only wwwroot (e.g. WEBSITE_RUN_FROM_PACKAGE=1). Renders will fail if the
                // package did not already carry the bit, so say so loudly rather than throwing
                // here and taking down startup for an app that may never render.
                _logger.LogWarning(ex, "Could not mark {Path} executable; renders may fail", path);
            }
        }
    }

    /// <summary>Builds a ProcessStartInfo, using the full exe path when FFmpegBinPath is configured.</summary>
    private ProcessStartInfo BuildPsi(string fileName, string arguments)
    {
        // When FFmpegBinPath is configured, resolve the full path to the executable so that
        // Windows can find it regardless of the current process's PATH environment variable.
        string resolvedFileName = fileName;
        if (!string.IsNullOrWhiteSpace(_binPath))
        {
            var exeName = OperatingSystem.IsWindows() ? fileName + ".exe" : fileName;
            var fullPath = Path.Combine(_binPath, exeName);
            if (File.Exists(fullPath))
                resolvedFileName = fullPath;
        }

        return new ProcessStartInfo
        {
            FileName = resolvedFileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }
}
