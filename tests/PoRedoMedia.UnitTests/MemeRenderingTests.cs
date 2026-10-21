using Microsoft.Extensions.Logging.Abstractions;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.MemeCaption;
using SkiaSharp;

namespace PoRedoMedia.UnitTests;

public sealed class MemeRenderingTests
{
    private static byte[] Png(int width, int height)
    {
        using var image = new SKBitmap(width, height);
        image.Erase(new SKColor(20, 40, 160));
        return ImageBytes.ToPng(image);
    }

    private static int WhitePixels(byte[] png, Func<int, int, int, int, bool> inRegion)
    {
        using var image = ImageBytes.Decode(png);
        var pixels = image.Pixels;
        var count = 0;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (inRegion(x, y, image.Width, image.Height) && pixels[y * image.Width + x] is { Red: > 240, Green: > 240, Blue: > 240 })
                    count++;
        return count;
    }

    [Fact]
    public void A_caption_is_drawn_in_white_at_the_top_and_bottom_and_the_size_is_kept()
    {
        var meme = MemeGenerator.Generate(Png(800, 600), "when you try to adult", "but you're still a kid at heart");

        Assert.Equal(new SKSizeI(800, 600), ImageBytes.Identify(meme));
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

    // Other decoders misread a JPEG written as one scan per component (Azure OpenAI saw a green
    // smear), so every JPEG this app sends out must carry all three components in a single scan.
    [Fact]
    public void Jpegs_sent_to_providers_use_one_interleaved_scan()
    {
        var jpeg = ImageBytes.ForProcessing(Png(3000, 1000));

        var scans = new List<int>();
        for (var i = 0; i < jpeg.Length - 4; i++)
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xDA)
                scans.Add(jpeg[i + 4]);

        Assert.Equal([3], scans);
        Assert.Equal(ImageBytes.MaxEdge, ImageBytes.Identify(jpeg)!.Value.Width);
    }
}
