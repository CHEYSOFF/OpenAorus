using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OpenAorus.App.Views;

/// <summary>
/// The small card the owner sees for the things Windows knows nothing about: fan mode, keyboard
/// backlight, the touchpad and the radio.
/// </summary>
/// <remarks>
/// <para>
/// Never for volume or display brightness. Windows draws those itself, and a second card on top
/// of the first is the complaint this whole release answers. Nothing here decides that - this
/// class is handed a string and shows it - and nothing here could reach it: the refusal lives in
/// <see cref="OpenAorus.Hardware.Hotkeys.HotkeyPolicy"/>, two layers upstream, and is pinned
/// against every settings file there is.
/// </para>
/// <para>
/// One window, reused. It is never activated and never takes hit-testing, so it cannot pull focus
/// out of whatever the owner is typing into and a click where it sits reaches what is behind it.
/// Three extended styles enforce that, and none of the three can be exercised without a desktop:
/// </para>
/// <list type="bullet">
///   <item><description><c>WS_EX_TRANSPARENT</c> - the window is not hit-tested at all, so a
///   click at the card's position is delivered to whatever is behind it, in any process.
///   <c>IsHitTestVisible="False"</c> in the markup only stops the card's own elements taking the
///   click; it does not stop the HWND taking it first.</description></item>
///   <item><description><c>WS_EX_NOACTIVATE</c> - the window refuses to become the foreground
///   window, so a keystroke aimed at another program is never lost and its caret never
///   moves. <c>ShowActivated="False"</c> covers only the moment of showing.</description></item>
///   <item><description><c>WS_EX_TOOLWINDOW</c> - keeps the card out of Alt-Tab.
///   <c>ShowInTaskbar="False"</c> covers only the taskbar.</description></item>
/// </list>
/// <para>
/// Full-screen games: a topmost, never-activated window either draws over one or is covered by
/// it, depending on how the game presents. Neither costs the game its focus, which is the part
/// that would matter - <c>WS_EX_NOACTIVATE</c> is what guarantees it. VERIFY 8 records which of
/// the two this chassis does.
/// </para>
/// </remarks>
public sealed partial class OverlayWindow : System.Windows.Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly DispatcherTimer _hide;

    public OverlayWindow()
    {
        InitializeComponent();
        _hide = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // The HWND exists by now and has not been shown yet, so the styles are in place before
        // the card is ever on screen - there is no first press that behaves differently.
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    /// <summary>Shows <paramref name="text"/> for <paramref name="seconds"/>, then hides.</summary>
    /// <remarks>
    /// A second call while one is up replaces the text and restarts the timer rather than stacking
    /// a second card - one keypress, one overlay at most, and a held fan key moves the words
    /// inside a card that does not blink.
    /// </remarks>
    /// <param name="text">What the card says.</param>
    /// <param name="seconds">How long it stays up.</param>
    public void ShowNotice(string text, int seconds)
    {
        NoticeText.Text = text;

        // Shown before it is placed, not after: SizeToContent means the card's size is only known
        // once WPF has laid the new text out, and laying it out needs the HWND that Show creates.
        // The window starts off screen (see the markup) so the frame between the two is nowhere.
        Show();
        UpdateLayout();

        var area = ScreenWorkArea();
        Left = OverlayPlacement.Left(area.Left, area.Width, ActualWidth);
        Top = OverlayPlacement.Top(area.Top, area.Height, ActualHeight);

        _hide.Stop();
        _hide.Interval = TimeSpan.FromSeconds(seconds);
        _hide.Start();

        Announce();
    }

    /// <summary>Tells a screen reader what the card says.</summary>
    /// <remarks>
    /// The card is marked as a live region in the markup, but WPF raises nothing when a
    /// <c>TextBlock</c>'s text changes, so a screen reader would never learn there was anything to
    /// read. Without this the overlay is the one part of the hotkey feature with no non-visual
    /// form at all - and it is the part that says what the keypress did.
    ///
    /// Guarded on <see cref="AutomationPeer.ListenerExists"/> so nothing is built when no
    /// assistive technology is running, which is the ordinary case and the one on the keypress
    /// path.
    /// </remarks>
    private void Announce()
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        UIElementAutomationPeer.CreatePeerForElement(NoticeText)
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>
    /// The work area of the screen the mouse is on, in device-independent units.
    /// </summary>
    /// <remarks>
    /// The pointer rather than the foreground window: a hotkey can fire with no window focused at
    /// all, and the pointer is the one thing that always names a screen the owner is looking at.
    ///
    /// Correct on a single-DPI desktop by construction. On a mixed-DPI one the transform comes
    /// from this window's own source, which is the scale of whichever screen it was last shown
    /// on - so the card can land off-centre the first time it appears on the other monitor. That
    /// is the one placement case that could not be reasoned out without hardware; VERIFY 7.8.
    /// </remarks>
    /// <returns>The work area in DIPs.</returns>
    private System.Windows.Rect ScreenWorkArea()
    {
        var mouse = System.Windows.Forms.Control.MousePosition;
        var screen = System.Windows.Forms.Screen.FromPoint(mouse);
        var w = screen.WorkingArea;

        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice;
        var scaleX = m?.M11 ?? 1.0;
        var scaleY = m?.M22 ?? 1.0;

        return new System.Windows.Rect(w.Left * scaleX, w.Top * scaleY, w.Width * scaleX, w.Height * scaleY);
    }

    /// <summary>Stops the timer on the way out, so a card in flight cannot tick into a closed window.</summary>
    /// <remarks>Reached when <c>Application.Shutdown</c> closes the app's windows: the main window
    /// hides to the tray rather than closing, so this is the only path that ever gets here.</remarks>
    /// <param name="e">Unused.</param>
    protected override void OnClosed(EventArgs e)
    {
        _hide.Stop();
        base.OnClosed(e);
    }

    // GWL_EXSTYLE is a 32-bit DWORD on every architecture, so the non-Ptr entry points are the
    // right ones here and stay right if this is ever built for anything but win-x64.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
