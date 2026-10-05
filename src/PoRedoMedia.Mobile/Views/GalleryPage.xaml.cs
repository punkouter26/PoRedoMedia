using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Mobile.ViewModels;

namespace PoRedoMedia.Mobile.Views;

public partial class GalleryPage : ContentPage
{
    public GalleryPage() : this(IPlatformApplication.Current?.Services.GetService<GalleryViewModel>()
        ?? throw new InvalidOperationException("GalleryViewModel is not registered"))
    {
    }

    public GalleryPage(GalleryViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // async void + try/catch: unhandled faults here would crash the app, and OnAppearing
        // cannot be awaited by the framework.
        try
        {
            if (BindingContext is GalleryViewModel vm)
                await vm.InitializeAsync();
        }
        catch
        {
            // Locked-out users still see the last status text; nothing to surface safely here.
        }
    }
}
