using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Mobile.Views;

namespace PoRedoMedia.Mobile;

public class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        FlyoutBehavior = FlyoutBehavior.Disabled;
        BackgroundColor = Color.FromArgb("#0B0F19");

        var tabBar = new TabBar();

        var snapTab = new Tab
        {
            Title = "Snap & Create",
            Items =
            {
                new ShellContent
                {
                    Title = "PoRedo",
                    Route = "MainPage",
                    ContentTemplate = new DataTemplate(() => services.GetRequiredService<MainPage>())
                }
            }
        };

        var settingsTab = new Tab
        {
            Title = "Settings",
            Items =
            {
                new ShellContent
                {
                    Title = "Settings",
                    Route = "SettingsPage",
                    ContentTemplate = new DataTemplate(() => services.GetRequiredService<SettingsPage>())
                }
            }
        };

        var galleryTab = new Tab
        {
            Title = "Gallery",
            Items =
            {
                new ShellContent
                {
                    Title = "My Gallery",
                    Route = "GalleryPage",
                    ContentTemplate = new DataTemplate(() => services.GetRequiredService<GalleryPage>())
                }
            }
        };

        tabBar.Items.Add(snapTab);
        tabBar.Items.Add(galleryTab);
        tabBar.Items.Add(settingsTab);

        Items.Add(tabBar);
    }
}

