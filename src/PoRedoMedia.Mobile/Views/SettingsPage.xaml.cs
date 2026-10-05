using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Mobile.ViewModels;

namespace PoRedoMedia.Mobile.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage() : this(IPlatformApplication.Current?.Services.GetService<SettingsViewModel>() ?? CreateFallbackViewModel())
    {
    }

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnTestConnectionClicked(object? sender, EventArgs e)
    {
        if (BindingContext is SettingsViewModel vm)
        {
            await vm.TestConnectionAsync();
        }
    }

    private void OnSaveSettingsClicked(object? sender, EventArgs e)
    {
        if (BindingContext is SettingsViewModel vm)
        {
            vm.SaveSettings();
            DisplayAlertAsync("Settings Saved", "Preferences updated successfully.", "OK");
        }
    }

    private static SettingsViewModel CreateFallbackViewModel()
    {
        var sp = IPlatformApplication.Current?.Services;
        if (sp != null)
        {
            var vm = ActivatorUtilities.CreateInstance<SettingsViewModel>(sp);
            return vm;
        }
        throw new InvalidOperationException("ServiceProvider not available");
    }
}
