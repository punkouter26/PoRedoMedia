using System.Text.Json;
using PoRedoMedia.Api.Features.PhotoToVideo;

namespace PoRedoMedia.UnitTests;

public sealed class VeoVideoServiceTests
{
    [Theory]
    [InlineData("The dog winks", "The dog winks Include ambient sound and natural audio that fits the scene.")]
    [InlineData("The dog barks and we HEAR it", "The dog barks and we HEAR it")]
    [InlineData("A silent pan across the room", "A silent pan across the room")]
    public void Sound_is_asked_for_unless_the_prompt_already_says_what_to_do_about_it(string prompt, string sent)
    {
        Assert.Equal(sent, VeoVideoService.WithAudioDirection(prompt));
    }

    [Fact]
    public void The_clip_address_is_read_from_a_finished_job_and_missing_when_the_job_was_blocked()
    {
        using var finished = JsonDocument.Parse(
            """{"done":true,"response":{"generateVideoResponse":{"generatedSamples":[{"video":{"uri":"https://x/clip"}}]}}}""");
        using var blocked = JsonDocument.Parse("""{"done":true,"response":{"generateVideoResponse":{"raiMediaFilteredCount":1}}}""");

        Assert.True(VeoVideoService.TryExtractVideoUri(finished.RootElement, out var uri));
        Assert.Equal("https://x/clip", uri);
        Assert.False(VeoVideoService.TryExtractVideoUri(blocked.RootElement, out _));
    }
}
