using Bunit;
using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Client.Shared;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;
using Radzen;
using static PoRedoMedia.Shared.Enums.MediaFunction;

namespace PoRedoMedia.UnitTests;

public sealed class FunctionPickerTests
{
    private static IRenderedComponent<FunctionPicker> Render(
        BunitContext ctx, MediaKind kind, MediaFunction[] ticked, MediaFunction[]? available = null, Action<IReadOnlyList<MediaFunction>>? changed = null)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        return ctx.Render<FunctionPicker>(p => p
            .Add(x => x.Kind, kind)
            .Add(x => x.Value, ticked)
            .Add(x => x.Available, available ?? Enum.GetValues<MediaFunction>())
            .Add(x => x.ValueChanged, changed ?? (_ => { })));
    }

    private static Dictionary<string, bool> Disabled(IRenderedComponent<FunctionPicker> cut) =>
        cut.FindAll("input[type=checkbox]").ToDictionary(c => c.Id!["fn-".Length..], c => c.HasAttribute("disabled"));

    [Fact]
    public void Only_the_functions_for_the_media_kind_are_offered()
    {
        using var ctx = new BunitContext();

        Assert.Equal(["Memeify", "VideoRoast", "Captions"], Disabled(Render(ctx, MediaKind.Video, [])).Keys);
    }

    [Fact]
    public void Ticking_bulk_disables_the_rest_and_ticking_anything_else_disables_bulk()
    {
        using var first = new BunitContext();
        using var second = new BunitContext();

        var withBulk = Disabled(Render(first, MediaKind.Image, [BulkStyles]));
        var withMeme = Disabled(Render(second, MediaKind.Image, [MemeCaption]));

        Assert.Equal(["Restyle", "MemeCaption", "RapRoast", "PhotoToVideo"], withBulk.Where(d => d.Value).Select(d => d.Key));
        Assert.Equal(["BulkStyles"], withMeme.Where(d => d.Value).Select(d => d.Key));
    }

    [Fact]
    public void A_function_the_server_cannot_run_is_disabled_and_says_so()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx, MediaKind.Image, [], available: [MemeCaption]);

        Assert.Equal(["MemeCaption"], Disabled(cut).Where(d => !d.Value).Select(d => d.Key));
        Assert.Contains("Not available on this server.", cut.Find("[data-fn=Restyle]").TextContent);
    }

    [Fact]
    public async Task Ticking_reports_the_selection_in_run_order()
    {
        using var ctx = new BunitContext();
        IReadOnlyList<MediaFunction>? reported = null;
        var cut = Render(ctx, MediaKind.Image, [RapRoast], changed: v => reported = v);

        await cut.Find("#fn-Restyle").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        Assert.Equal([Restyle, RapRoast], reported);
    }

    [Theory]
    [InlineData("ai", "", "", null)]
    [InlineData("text", "", "", "Type the top or the bottom text for the meme.")]
    [InlineData("text", "MemeCaption.bottom", "hi", null)]
    [InlineData("template", "", "", "Pick a meme template.")]
    public void Options_that_cannot_run_are_caught_before_a_credit_is_spent(string mode, string key, string value, string? expected)
    {
        var options = new Dictionary<string, string> { [RunOptions.MemeMode] = mode };
        if (key.Length > 0)
            options[key] = value;

        Assert.Equal(expected, FunctionOptions.Missing([MemeCaption], options));
    }
}
