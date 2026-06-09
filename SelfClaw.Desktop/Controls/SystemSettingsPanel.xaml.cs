using System.Windows.Controls;
using SelfClaw.Desktop.ViewModels;

namespace SelfClaw.Desktop.Controls;

public partial class SystemSettingsPanel : UserControl
{
    public SystemSettingsPanel()
    {
        InitializeComponent();
    }

    private async void OnLoadedAsync(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SystemSettingsViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private async void OnAutoSaveLostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SystemSettingsViewModel viewModel)
        {
            await viewModel.SaveSelectedProviderAsync();
        }
    }
}
