using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Client.Pages;
using PoRedoMedia.Client.Services;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;
using Radzen;

namespace PoRedoMedia.UnitTests;

public sealed class GalleryTests
{
    private static MediaDto Item(string title, MediaKind kind) => new(
        Guid.NewGuid(), kind, title, "Upload", null, "x/y", 1, null, false, false, DateTimeOffset.UtcNow, "/content", "/thumb");

    [Theory]
    [InlineData("All", "", 3)]
    [InlineData("Video", "", 1)]
    [InlineData("All", "BEACH", 1)]
    [InlineData("Image", "party", 0)]
    public void The_filter_narrows_by_kind_and_by_title(string kind, string search, int expected)
    {
        MediaDto[] items = [Item("beach.jpg", MediaKind.Image), Item("party.mp4", MediaKind.Video), Item("Roast · dog", MediaKind.Audio)];

        Assert.Equal(expected, Gallery.Filter(items, kind, search).Count());
    }

    [Fact]
    public async Task Each_item_is_shown_with_its_thumbnail_title_and_kind()
    {
        // Disposed asynchronously: the run tracker the gallery listens to can only be disposed that way.
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        var json = """
            [{"id":"11111111-1111-1111-1111-111111111111","kind":"Image","title":"beach.jpg","origin":"Upload","contentType":"image/jpeg",
              "sizeBytes":1,"pinned":false,"shared":false,"createdAt":"2026-10-05T00:00:00+00:00","url":"/c1","thumbUrl":"/t1"},
             {"id":"22222222-2222-2222-2222-222222222222","kind":"Video","title":"party.mp4","origin":"Upload","contentType":"video/mp4",
              "sizeBytes":1,"pinned":false,"shared":false,"createdAt":"2026-10-05T00:00:00+00:00","url":"/c2","thumbUrl":"/t2"}]
            """;
        ctx.Services.AddSingleton(new MediaApi(new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("http://app/") }));
        ctx.Services.AddScoped<BlobUploadService>();
        ctx.Services.AddScoped(services => new RunApi(
            new HttpClient(new StubHandler("[]")) { BaseAddress = new Uri("http://app/") }, services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()));
        ctx.Services.AddScoped<RunTracker>();

        var cut = ctx.Render<Gallery>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["/t1", "/t2"], cut.FindAll("img.gallery-thumb").Select(i => i.GetAttribute("src")));
            Assert.Contains("beach.jpg", cut.Markup);
            Assert.Contains("Video", cut.Markup);
        });
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
