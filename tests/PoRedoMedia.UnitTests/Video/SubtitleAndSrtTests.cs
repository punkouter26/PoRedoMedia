
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Api.Features.VideoRoast;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.UnitTests;

/// <summary>Speech subtitles: line fitting for drawtext, SRT export, and the filter they produce.</summary>
public sealed class SubtitleAndSrtTests
{
    [Fact]
    public void SubtitleChunker_SplitsLongSegmentIntoShortCardsCoveringTheSameSpan()
    {
        var segment = new TranscriptSegmentDto(2.0, 8.0,
            "so I told him that there is absolutely no way this is going to work out for anyone involved");

        var cards = SubtitleChunker.Chunk([segment]);

        Assert.True(cards.Count > 1);
        Assert.All(cards, c => Assert.True(c.Text.Length <= SubtitleChunker.MaxCharsPerCard, c.Text));
        Assert.Equal(2.0, cards[0].StartSeconds, 3);
        Assert.Equal(8.0, cards[^1].EndSeconds, 3);
        // Contiguous: each card starts where the previous one ended.
        for (var i = 1; i < cards.Count; i++)
            Assert.Equal(cards[i - 1].EndSeconds, cards[i].StartSeconds, 6);
        Assert.Equal(segment.Text, string.Join(' ', cards.Select(c => c.Text)));
    }

    [Theory]
    [InlineData(0, "00:00:00,000")]
    [InlineData(3725.042, "01:02:05,042")]
    public void SrtWriter_Timestamp_UsesCommaBeforeMilliseconds(double seconds, string expected)
        => Assert.Equal(expected, SrtWriter.Timestamp(seconds));

    [Fact]
    public void BuildVideoFilterChain_WithSubtitles_AddsBoxedDrawtextInsideEachLineWindow()
    {
        var chain = FFmpegArgs.BuildVideoFilterChain(
            [],
            aggressiveVisuals: false,
            subtitles: [new TranscriptSegmentDto(1.25, 3.5, "don't do it")]);

        Assert.Contains("box=1", chain, StringComparison.Ordinal);
        Assert.Contains("between(t\\,1.25\\,3.5)", chain, StringComparison.Ordinal);
        // Apostrophes would close drawtext's single-quoted text early — they must be stripped.
        Assert.Contains("text='dont do it'", chain, StringComparison.Ordinal);
    }
}
