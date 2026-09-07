using System.ComponentModel;
using System.Windows;
using OpenAorus.App.ViewModels;

namespace OpenAorus.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>The one overlay card, built the first time a hotkey asks for one.</summary>
    /// <remarks>
    /// Owned here rather than by the view model because it is a window, and lazily because most
    /// runs never show one: every overlay is off by default, so an owner who has not switched any
    /// on never pays for the HWND. Not closed when this window closes - closing hides to the tray,
    /// and taking the overlay down with it would leave the Fn row silent for the rest of the
    /// session. <c>Application.Shutdown</c> closes it along with everything else on the way out.
    /// </remarks>
    private OverlayWindow? _overlay;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        ContextHost.Content = new ContextPanel { DataContext = vm };
        BatteryHost.Content = new BatteryPanel { DataContext = vm.Battery };
        LightingHost.Content = new LightingPanel { DataContext = vm.Lighting };
        IsVisibleChanged += (_, _) => _vm.IsWindowVisible = IsVisible;
        vm.OverlayRequested += ShowOverlay;
    }

    /// <summary>Puts one notice on screen for as long as the settings currently say.</summary>
    /// <remarks>
    /// <para>
    /// The dispatcher hop is not belt and braces. <see cref="Hotkeys.HotkeyService"/> posts every
    /// action to the UI thread, so this normally arrives on it already - but the view model awaits
    /// a fan-mode apply before raising this, and a continuation is only guaranteed back on the
    /// dispatcher while there is a synchronisation context to come back to. <c>Invoke</c> on the
    /// thread that already owns the dispatcher runs inline, so the ordinary path costs nothing.
    /// </para>
    /// <para>
    /// The duration is read live rather than captured, so a change in the Settings window applies
    /// to the next press rather than the next launch - the same rule the overlay switches follow.
    /// </para>
    /// </remarks>
    /// <param name="text">What the policy decided the card should say.</param>
    private void ShowOverlay(string text) =>
        Dispatcher.Invoke(() =>
        {
            _overlay ??= new OverlayWindow();
            _overlay.ShowNotice(text, App.Services.Settings.Hotkeys.OverlaySeconds);
        });

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
