using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Mobile.Services;
using PoRedoMedia.Mobile.ViewModels;

namespace PoRedoMedia.Mobile.Views;

public partial class MainPage : ContentPage
{
    public MainPage() : this(IPlatformApplication.Current?.Services.GetService<MainViewModel>() ?? CreateFallbackViewModel())
    {
    }

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // async void on a framework callback: everything is guarded — an unhandled fault here
        // would take the app down rather than surface as an in-page error.
        try
        {
            if (BindingContext is MainViewModel vm)
                await vm.OnAppearingAsync();
        }
        catch
        {
            // Entry-point failures (unreadable share, denied camera) surface in the page's own
            // error banner from the view model; nothing more to do here.
        }
    }

    private async void OnProShotClicked(object? sender, EventArgs e)
    {
        // Pro camera-pipeline controls (torch, tap-to-focus, pinch zoom) were deferred
        // because the AndroidX CameraX binding API surface did not line up with the docs we
        // developed against (see SPEC.md §15). For now the "Pro shot" button just opens the
        // standard camera capture; the user goal — start a session with a single tap — still
        // works, even though torch and AF are not exposed.
        try
        {
            if (BindingContext is MainViewModel vm && vm.TakePhotoCommand.CanExecute(null))
                vm.TakePhotoCommand.Execute(null);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Pro capture failed", ex.Message, "OK");
        }
    }

    private static MainViewModel CreateFallbackViewModel()
    {
        var sp = IPlatformApplication.Current?.Services;
        if (sp != null)
        {
            var vm = ActivatorUtilities.CreateInstance<MainViewModel>(sp);
            return vm;
        }
        throw new InvalidOperationException("ServiceProvider not available");
    }
}
