using PoRedoMedia.Api.Features.Memeify;

using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Api.Features.VideoRoast;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.UnitTests;

public sealed class RoastServiceTests
{
    [Fact]
    public void Schedule_KeepsJokesApartInsideTheClipAndUnderThirtySeconds()
    {
        // A 40 s clip. Each clip: (its moment, how long it plays).
        var placed = RoastService.Schedule(
            [
                (0, 5_000),       // plays at its moment
                (2_000, 5_000),   // previous joke still running → pushed behind it
                (38_000, 5_000),  // would run past the end → pulled back to end with the clip
                (39_000, 5_000),  // no room left before the end → dropped
            ],
            videoMs: 40_000);

        Assert.Equal([(0, 0L), (1, 5_250L), (2, 35_000L)], placed);

        // Seven 5 s jokes with room for all: the seventh would be second 31–35 of speech.
        var capped = RoastService.Schedule([.. Enumerable.Range(0, 7).Select(i => (i * 10_000L, 5_000))], videoMs: 600_000);
        Assert.Equal(6, capped.Count);
    }
}
