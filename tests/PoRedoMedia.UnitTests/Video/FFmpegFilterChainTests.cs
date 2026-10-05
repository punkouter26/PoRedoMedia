using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Api.Features.VideoRoast;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.UnitTests;

/// <summary>
/// Covers the pure string building in <see cref="FFmpegArgs"/>. This is the
/// highest-risk logic in the render path — a malformed filter chain fails at the ffmpeg
/// process boundary, where the only signal is a non-zero exit code and a stderr dump.
/// </summary>
public sealed class FFmpegFilterChainTests
{
    private static RenderVisualEntry Sound(long ms = 1000, string? effect = null, string? caption = null, string? position = null)
        => new(ms, "/tmp/sound.mp3", effect, caption, position);

    // ── Aspect-ratio framing ──────────────────────────────────────────────

    // ── Visual effects ────────────────────────────────────────────────────

    [Fact]
    public void BuildVideoFilterChain_AggressiveVisuals_AddsDeepFryAndUnsharp()
    {
        var chain = FFmpegArgs.BuildVideoFilterChain([Sound()], aggressiveVisuals: true);

        Assert.Contains("eq=saturation=3", chain, StringComparison.Ordinal);
        Assert.Contains("unsharp=", chain, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFFmpegArgs_RepeatedCueEffect_GetsOneWindowedPassPerCue()
    {
        var entries = new List<RenderVisualEntry>
        {
            new(TimestampMs: 1000, SoundPath: "/tmp/a.mp3", VisualEffect: "MotionBlur", CaptionText: null, CaptionPosition: null),
            new(TimestampMs: 2000, SoundPath: "/tmp/b.mp3", VisualEffect: "MotionBlur", CaptionText: null, CaptionPosition: null)
        };

        var args = FFmpegArgs.BuildFFmpegArgs(
            sourcePath: "/tmp/source.mp4",
            entries: entries,
            outputPath: "/tmp/out.mp4",
            aggressiveVisuals: false,
            sourceDurationSeconds: 10.0,
            sourceHasAudio: true);

        // One pass per cue — not deduplicated per effect, since each needs its own window.
        Assert.Equal(2, args.Split("tblend=all_mode=average").Length - 1);
        Assert.Contains("between(t\\,1\\,3.5)", args, StringComparison.Ordinal);
        Assert.Contains("between(t\\,2\\,4.5)", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFFmpegArgs_RoastVoice_DucksSourceAndMemeSoundsUnderIt()
    {
        var entries = new List<RenderVisualEntry>
        {
            new(TimestampMs: 1000, SoundPath: "/tmp/boom.mp3", VisualEffect: null, CaptionText: null, CaptionPosition: null),
            new(TimestampMs: 3000, SoundPath: "/tmp/joke.mp3", VisualEffect: null, CaptionText: null, CaptionPosition: null, Voice: true)
        };

        var args = FFmpegArgs.BuildFFmpegArgs("/tmp/source.mp4", entries, "/tmp/out.mp4", aggressiveVisuals: false, sourceDurationSeconds: 10.0, sourceHasAudio: true);

        // The meme sound drops, and it joins the source in the bed the voice ducks.
        Assert.Contains("[1:a]adelay=1000|1000,volume=0.3[a0]", args, StringComparison.Ordinal);
        Assert.Contains("[2:a]adelay=3000|3000,volume=2[a1]", args, StringComparison.Ordinal);
        Assert.Contains("[aorig][a0]amix=inputs=2:normalize=0:duration=longest[bed];[bed][voxkey]sidechaincompress", args, StringComparison.Ordinal);
        Assert.Contains("[ducked][vox]amix=inputs=2", args, StringComparison.Ordinal);
    }

    // ── Captions ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildVideoFilterChain_Caption_IsUppercasedAndTimeWindowed()
    {
        var chain = FFmpegArgs.BuildVideoFilterChain(
            [Sound(2000, caption: "oh no")], aggressiveVisuals: false);

        Assert.Contains("drawtext=text='OH NO'", chain, StringComparison.Ordinal);
        // 2.5 s display window starting at the cue timestamp.
        Assert.Contains("between(t\\,2\\,4.5)", chain, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVideoFilterChain_BlankCaption_AddsNoDrawtext()
    {
        var chain = FFmpegArgs.BuildVideoFilterChain(
            [Sound(caption: "   ")], aggressiveVisuals: false);

        Assert.DoesNotContain("drawtext", chain, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVideoFilterChain_TimestampsUseInvariantCulture()
    {
        // A comma decimal separator here would split the filter chain, since ',' is the
        // filtergraph separator.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var chain = FFmpegArgs.BuildVideoFilterChain(
                [Sound(1500, caption: "hi")], aggressiveVisuals: false);

            Assert.Contains("between(t\\,1.5\\,4)", chain, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void BuildVideoFilterChain_FiltersAreCommaJoined()
    {
        var chain = FFmpegArgs.BuildVideoFilterChain(
            [Sound(effect: "MotionBlur")], aggressiveVisuals: true);

        // scale, eq, unsharp → three filters, two separators. Escaped commas
        // (\,) inside a filter's own arguments do not separate filters, so they are
        // neutralised before splitting.
        var filters = chain.Replace("\\,", "").Split(',');
        Assert.Equal(3, filters.Length);
        Assert.StartsWith("scale=", filters[0], StringComparison.Ordinal);
        Assert.StartsWith("eq=", filters[1], StringComparison.Ordinal);
        Assert.StartsWith("unsharp=", filters[2], StringComparison.Ordinal);
    }

    // ── drawtext escaping ─────────────────────────────────────────────────

    [Fact]
    public void SanitizeForDrawtext_FlattensNewlinesToSpaces()
    {
        Assert.Equal("a b", FFmpegArgs.SanitizeForDrawtext("a\r\nb"));
    }

    [Fact]
    public void SanitizeForDrawtext_DoublesBackslashThenDropsQuote()
    {
        // Order matters: the backslash pass runs first, so the caller's own backslash is
        // doubled and the apostrophe is then dropped, leaving \\ and nothing else. Running
        // the backslash pass last would instead double any backslash a later escape pass
        // introduced, turning \: back into \\:.
        Assert.Equal(@"\\", FFmpegArgs.SanitizeForDrawtext(@"\'"));
    }

    // ── Windowed cue effects & Overlay filters ────────────────────────────

    [Fact]
    public void BuildFFmpegArgs_WithWindowedEffectAndOverlay_ChainsFiltersAndInputs()
    {
        var entries = new List<RenderVisualEntry>
        {
            new(
                TimestampMs: 1000,
                SoundPath: "/tmp/boom.mp3",
                VisualEffect: "MotionBlur",
                CaptionText: "BOOM",
                CaptionPosition: "Top",
                OverlayPath: "/tmp/deal-with-it.png",
                OverlayX: 0.3,
                OverlayY: 0.4,
                OverlayScale: 1.2)
        };

        var args = FFmpegArgs.BuildFFmpegArgs(
            sourcePath: "/tmp/source.mp4",
            entries: entries,
            outputPath: "/tmp/out.mp4",
            aggressiveVisuals: false,
            sourceDurationSeconds: 10.0,
            sourceHasAudio: true);

        // Inputs should include source, sound, and overlay
        Assert.Contains("-i \"/tmp/source.mp4\"", args, StringComparison.Ordinal);
        Assert.Contains("-i \"/tmp/boom.mp3\"", args, StringComparison.Ordinal);
        Assert.Contains("-i \"/tmp/deal-with-it.png\"", args, StringComparison.Ordinal);

        // Filter complex should chain split, the effect, and a time-gated overlay
        Assert.Contains("split", args, StringComparison.Ordinal);
        Assert.Contains("tblend=all_mode=average", args, StringComparison.Ordinal);
        Assert.Contains("overlay=", args, StringComparison.Ordinal);
        // The effect must be confined to its cue's window, not smeared over the whole video.
        Assert.Contains("overlay=enable='between(t\\,1\\,3.5)'", args, StringComparison.Ordinal);
        Assert.Contains("-map \"[vout]\"", args, StringComparison.Ordinal);
        Assert.Contains("-map \"[aout]\"", args, StringComparison.Ordinal);
    }
}
