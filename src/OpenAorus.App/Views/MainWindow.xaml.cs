using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
            // The same trace both channels write to: a card that failed to become click-through
            // belongs in the dump beside the reports that asked for it.
            _overlay ??= new OverlayWindow(App.Services.HotkeyTrace);
            _overlay.ShowNotice(text, App.Services.Settings.Hotkeys.OverlaySeconds);
        });

    /// <summary>Close hides to tray; Quit in the tray menu really exits.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Decides what a click on the tray icon should do.
    /// </summary>
    /// <remarks>
    /// Hiding on "visible" alone is what made the icon need two clicks. A window the owner has
    /// clicked away from is still visible, just behind something - so the first click hid what
    /// they were trying to raise and the second one brought it back. Foreground is the condition
    /// that matches the intent: hide the window you are looking at, raise the one you are not.
    /// </remarks>
    /// <param name="isVisible">Whether the window is shown at all.</param>
    /// <param name="isMinimized">Whether it is minimised.</param>
    /// <param name="isForeground">Whether it is the window with focus.</param>
    /// <returns>True to hide it, false to raise it.</returns>
    internal static bool ShouldHideOnTrayClick(bool isVisible, bool isMinimized, bool isForeground) =>
        isVisible && !isMinimized && isForeground;

    public void ToggleVisibility()
    {
        if (ShouldHideOnTrayClick(IsVisible, WindowState == WindowState.Minimized, IsForeground()))
        {
            Hide();
            return;
        }

        PlaceNearTray();
        Show();
        WindowState = WindowState.Normal;
        Activate();

        // Windows refuses foreground to a process the user did not just interact with, and a tray
        // click counts as interacting with the shell rather than with us. A brief topmost flip is
        // the ordinary way to get in front without SetForegroundWindow's rules applying.
        if (!IsForeground())
        {
            Topmost = true;
            Topmost = false;
        }
    }

    private bool IsForeground()
    {
        var mine = new WindowInteropHelper(this).Handle;
        return mine != nint.Zero && mine == GetForegroundWindow();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

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
