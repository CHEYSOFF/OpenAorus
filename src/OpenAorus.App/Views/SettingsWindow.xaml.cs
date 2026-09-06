using System.Windows;
using OpenAorus.App.ViewModels;

namespace OpenAorus.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.RefreshAsync();
    }
}
