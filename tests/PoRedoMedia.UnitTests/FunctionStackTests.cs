using PoRedoMedia.Shared;
using PoRedoMedia.Shared.Enums;
using static PoRedoMedia.Shared.Enums.MediaFunction;

namespace PoRedoMedia.UnitTests;

public sealed class FunctionStackTests
{
    private static readonly MediaFunction[] ImageFunctions = [Restyle, MemeCaption, RapRoast, PhotoToVideo, BulkStyles];
    private static readonly MediaFunction[] VideoFunctions = [Memeify, VideoRoast, Captions];

    // SPEC 2.1, restated independently of the implementation and checked for every one of the
    // 255 non-empty subsets of the eight functions, against each media kind.
    [Theory]
    [InlineData(MediaKind.Image)]
    [InlineData(MediaKind.Video)]
    [InlineData(MediaKind.Audio)]
    public void Every_combination_is_accepted_or_refused_as_the_spec_table_says(MediaKind kind)
    {
        var all = Enum.GetValues<MediaFunction>();
        for (var mask = 1; mask < 1 << all.Length; mask++)
        {
            var stack = all.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            var allowedForKind = kind switch
            {
                MediaKind.Image => stack.All(ImageFunctions.Contains),
                MediaKind.Video => stack.All(VideoFunctions.Contains),
                _ => false,
            };
            var bulkIsAlone = !stack.Contains(BulkStyles) || stack.Length == 1;

            Assert.True(
                (allowedForKind && bulkIsAlone) == (FunctionStack.Validate(kind, stack) is null),
                $"{kind}: [{string.Join(", ", stack)}] -> {FunctionStack.Validate(kind, stack) ?? "accepted"}");
        }
    }

    [Fact]
    public void An_empty_or_repeated_stack_is_refused()
    {
        Assert.NotNull(FunctionStack.Validate(MediaKind.Image, []));
        Assert.NotNull(FunctionStack.Validate(MediaKind.Image, [Restyle, Restyle]));
    }

    [Fact]
    public void Image_steps_run_in_the_fixed_order_whatever_order_they_were_ticked_in()
    {
        Assert.Equal([Restyle, MemeCaption, RapRoast, PhotoToVideo], FunctionStack.InRunOrder([PhotoToVideo, MemeCaption, RapRoast, Restyle]));
    }

    [Fact]
    public void Ticking_bulk_blocks_everything_else_and_anything_else_blocks_bulk()
    {
        Assert.False(FunctionStack.CanAdd([BulkStyles], Restyle));
        Assert.False(FunctionStack.CanAdd([MemeCaption], BulkStyles));
        Assert.True(FunctionStack.CanAdd([MemeCaption], RapRoast));
        Assert.False(FunctionStack.CanAdd([Memeify], Restyle));
        Assert.Equal(VideoFunctions, FunctionStack.For(MediaKind.Video));
    }
}
