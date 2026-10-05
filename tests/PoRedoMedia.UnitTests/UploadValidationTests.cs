using PoRedoMedia.Api.Features.Media;
using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.UnitTests;

public sealed class UploadValidationTests
{
    [Theory]
    [InlineData("beach.JPG", 1_000, MediaKind.Image, ".jpg", "image/jpeg")]
    [InlineData("a.b.png", 1_000, MediaKind.Image, ".png", "image/png")]
    [InlineData("party.mp4", 50_000_000, MediaKind.Video, ".mp4", "video/mp4")]
    [InlineData("clip.mov", 1_000, MediaKind.Video, ".mov", "video/quicktime")]
    public void An_accepted_file_is_classified_by_its_extension(string name, long size, MediaKind kind, string extension, string contentType)
    {
        var result = UploadValidation.Classify(name, size);

        Assert.Null(result.Error);
        Assert.Equal((kind, extension, contentType), (result.Kind, result.Extension, result.ContentType));
    }

    [Theory]
    [InlineData("virus.exe", 1_000, "type")]
    [InlineData("noextension", 1_000, "type")]
    [InlineData("big.png", 10 * 1024 * 1024 + 1, "10 MB")]
    [InlineData("big.mp4", 200L * 1024 * 1024 + 1, "200 MB")]
    [InlineData("empty.png", 0, "empty")]
    public void A_refused_file_gets_a_reason_that_states_the_limit(string name, long size, string reasonContains)
    {
        Assert.Contains(reasonContains, UploadValidation.Classify(name, size).Error);
    }
}
