using Bunit;
using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Client.Pages;
using PoRedoMedia.Client.Services;
using Radzen;

namespace PoRedoMedia.UnitTests;

public sealed class SettingsTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        ctx.Services.AddSingleton(new HttpClient());
        ctx.Services.AddSingleton<AppSettings>();
        return ctx;
    }

    [Fact]
    public void A_signed_in_user_sees_every_advanced_switch_and_all_start_off()
    {
        using var ctx = NewContext();
        ctx.AddAuthorization().SetAuthorized("someone@example.com");

        var advanced = ctx.Render<Settings>().Find("section[aria-labelledby=settings-advanced]");

        Assert.Equal(AppSettings.Advanced.Select(s => s.Label), advanced.QuerySelectorAll("label").Select(l => l.TextContent.Trim()));
        Assert.Equal(AppSettings.Advanced.Length, advanced.QuerySelectorAll(".rz-switch").Length);
        Assert.Empty(advanced.QuerySelectorAll(".rz-switch-checked"));
    }

    [Fact]
    public void Before_sign_in_only_the_look_and_sound_show()
    {
        using var ctx = NewContext();
        ctx.AddAuthorization();

        var cut = ctx.Render<Settings>();

        Assert.NotNull(cut.Find("section[aria-labelledby=settings-look]"));
        Assert.Empty(cut.FindAll("section[aria-labelledby=settings-advanced]"));
        Assert.Empty(cut.FindAll(".settings-signout"));
    }
}
