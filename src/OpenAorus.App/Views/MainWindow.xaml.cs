using System.ComponentModel;
using System.Windows;
using OpenAorus.App.ViewModels;

namespace OpenAorus.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        ContextHost.Content = new ContextPanel { DataContext = vm };
        BatteryHost.Content = new BatteryPanel { DataContext = vm.Battery };
        IsVisibleChanged += (_, _) => _vm.IsWindowVisible = IsVisible;
    }

    /// <summary>Close hides to tray; Quit in the tray menu really exits.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    public void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized) { Hide(); return; }
        PlaceNearTray();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void PlaceNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_vm.SettingsVm) { Owner = this };
        w.ShowDialog();
    }
}
