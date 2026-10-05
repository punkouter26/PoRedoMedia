using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PoRedoMedia.Api.Common;

namespace PoRedoMedia.IntegrationTests;

public sealed class FFmpegProcessTests
{
    [FFmpegFact]
    public async Task A_rendered_clip_can_be_probed_for_duration_and_audio()
    {
        var ffmpeg = new FFmpegProcess(new ConfigurationBuilder().Build(), NullLogger<FFmpegProcess>.Instance);
        var clip = Path.Combine(Path.GetTempPath(), $"poredomedia-{Guid.NewGuid():N}.mp4");
        try
        {
            var exit = await ffmpeg.RunAsync(
                $"-y -f lavfi -i testsrc=duration=1:size=160x120:rate=10 -f lavfi -i sine=frequency=440:duration=1 -shortest \"{clip}\"",
                "test", CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.InRange(await ffmpeg.DurationSecondsAsync(clip, CancellationToken.None), 0.9, 1.2);
            Assert.True(await ffmpeg.HasAudioStreamAsync(clip, CancellationToken.None));
        }
        finally
        {
            File.Delete(clip);
        }
    }

    [FFmpegFact]
    public async Task A_file_that_is_not_media_probes_as_zero_seconds()
    {
        var ffmpeg = new FFmpegProcess(new ConfigurationBuilder().Build(), NullLogger<FFmpegProcess>.Instance);
        var notMedia = Path.GetTempFileName();
        try
        {
            Assert.Equal(0, await ffmpeg.DurationSecondsAsync(notMedia, CancellationToken.None));
        }
        finally
        {
            File.Delete(notMedia);
        }
    }
}

/// <summary>A fact that needs ffmpeg on PATH. Without it the test is reported as skipped.</summary>
public sealed class FFmpegFactAttribute : FactAttribute
{
    private static readonly bool Available = Probe();

    public FFmpegFactAttribute()
    {
        if (!Available)
            Skip = "ffmpeg is not on PATH.";
    }

    private static bool Probe()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
