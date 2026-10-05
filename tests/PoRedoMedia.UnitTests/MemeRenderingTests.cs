using Microsoft.Extensions.Logging.Abstractions;
using PoRedoMedia.Api.Features.MemeCaption;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoRedoMedia.UnitTests;

public sealed class MemeRenderingTests
{
    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(20, 40, 160));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static int WhitePixels(byte[] png, Func<int, int, int, int, bool> inRegion)
    {
        using var image = Image.Load<Rgba32>(png);
        var count = 0;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (inRegion(x, y, image.Width, image.Height) && image[x, y] is { R: > 240, G: > 240, B: > 240 })
                    count++;
        return count;
    }

    [Fact]
    public void A_caption_is_drawn_in_white_at_the_top_and_bottom_and_the_size_is_kept()
    {
        var meme = MemeGenerator.Generate(Png(800, 600), "when you try to adult", "but you're still a kid at heart");

        using var image = Image.Load(meme);
        Assert.Equal((800, 600), (image.Width, image.Height));
        Assert.True(WhitePixels(meme, (_, y, _, h) => y < h * 0.3) > 200, "no text at the top");
        Assert.True(WhitePixels(meme, (_, y, _, h) => y > h * 0.6) > 200, "no text at the bottom");
    }

    [Theory]
    [InlineData("WHEN YOU TRY TO ADULT BUT YOU ARE STILL VERY MUCH A KID AT HEART AND EVERYONE KNOWS IT")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Long_text_is_shrunk_and_wrapped_so_none_of_it_touches_the_left_or_right_edge(string text)
    {
        var meme = MemeGenerator.Generate(Png(800, 600), text, null);

        Assert.True(WhitePixels(meme, (_, _, _, _) => true) > 200, "nothing was drawn");
        Assert.Equal(0, WhitePixels(meme, (x, _, w, _) => x < 4 || x >= w - 4));
    }

    [Fact]
    public void Blank_text_leaves_the_picture_untouched_and_no_image_is_refused()
    {
        Assert.Equal(0, WhitePixels(MemeGenerator.Generate(Png(200, 200), " ", null), (_, _, _, _) => true));
        Assert.Throws<ArgumentException>(() => MemeGenerator.Generate([], "top", "bottom"));
    }

    [Fact]
    public void The_template_library_is_well_formed()
    {
        var templates = new MemeTemplateService(NullLogger<MemeTemplateService>.Instance).GetTemplates();

        Assert.Equal(20, templates.Count);
        Assert.Equal(templates.Count, templates.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(templates, t =>
        {
            Assert.InRange(t.RequiredZoneCount, 1, t.Zones.Count);
            Assert.All(t.Zones, z => Assert.True(z.X is >= 0 and <= 1 && z.Y is >= 0 and <= 1 && z.MaxWidthRatio is > 0 and <= 1, $"{t.Id}: {z.Label}"));
        });
    }

    [Fact]
    public async Task A_template_draws_its_lines_and_refuses_too_few()
    {
        var service = new MemeTemplateService(NullLogger<MemeTemplateService>.Instance);
        var template = service.GetTemplates().First(t => t.RequiredZoneCount >= 2);
        var lines = template.Zones.Select(z => "some text").ToList();

        var (meme, contentType) = await service.RenderAsync(Png(600, 600), template, lines);

        Assert.Equal("image/png", contentType);
        Assert.True(WhitePixels(meme, (_, _, _, _) => true) > 200);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenderAsync(Png(600, 600), template, []));
    }
}
