using System.Text.Json;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Common.Ai;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoRedoMedia.UnitTests;

public sealed class GeminiImageServiceTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void The_picture_is_read_from_the_first_image_part()
    {
        var image = GeminiImageService.Read(Json(
            """{"candidates":[{"content":{"parts":[{"inlineData":{"mimeType":"image/jpeg","data":"AQID"}}]}}]}"""));

        Assert.Equal([1, 2, 3], image.Data);
        Assert.Equal(("image/jpeg", ".jpg"), (image.ContentType, image.Extension));
    }

    [Theory]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY"}]}""", "safety filter")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"REFUSE: I cannot draw that."}]}}]}""", "declined: I cannot draw that.")]
    [InlineData("""{"candidates":[{"content":{"parts":[]}}]}""", "returned no picture")]
    [InlineData("""{"promptFeedback":{"blockReason":"OTHER"}}""", "declined this request")]
    public void A_refusal_becomes_a_failure_the_user_can_read(string response, string reasonContains)
    {
        var error = Assert.Throws<RunStepException>(() => GeminiImageService.Read(Json(response)));

        Assert.Contains(reasonContains, error.Message);
    }

    [Theory]
    [InlineData(800, 600, "4:3")]
    [InlineData(600, 800, "3:4")]
    [InlineData(1000, 1000, "1:1")]
    [InlineData(1920, 1080, "16:9")]
    public void The_new_picture_is_asked_for_in_the_nearest_supported_shape(int width, int height, string expected)
    {
        using var image = new Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);

        Assert.Equal(expected, GeminiImageService.AspectRatioOf(stream.ToArray()));
        Assert.Null(GeminiImageService.AspectRatioOf([1, 2, 3]));
    }
}
