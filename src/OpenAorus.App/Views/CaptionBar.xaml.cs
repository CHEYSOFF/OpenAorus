using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace OpenAorus.App.Views;

/// <summary>
/// The window's own minimise and close buttons, drawn in the dark theme.
/// </summary>
/// <remarks>
/// <para>
/// The window they sit in keeps its real Win32 frame - see the <c>WindowChrome</c> on
/// <see cref="MainWindow"/> - so this control paints the caption and nothing more. Dragging,
/// the system menu on right-click, Alt+Space and the resize borders are all still Windows'
/// own behaviour on a caption region, which is why none of it is reimplemented here.
/// </para>
/// <para>
/// The two clicks go through <see cref="SystemCommands"/> rather than setting
/// <see cref="Window.WindowState"/> or calling <see cref="Window.Close"/> directly: they post
/// the same <c>SC_MINIMIZE</c> and <c>SC_CLOSE</c> the frame's own buttons posted, so the
/// minimise animation is the system one and close still runs through
/// <see cref="MainWindow.OnClosing"/>, which cancels and hides to the tray.
/// </para>
/// </remarks>
public partial class CaptionBar : UserControl
{
    /// <summary>Whether this window offers a minimise button. False for the settings dialog,
    /// which the Windows frame gave no minimise button either.</summary>
    public static readonly DependencyProperty CanMinimiseProperty =
        DependencyProperty.Register(nameof(CanMinimise), typeof(bool), typeof(CaptionBar),
            new PropertyMetadata(true));

    /// <summary>What closing this particular window actually does; shown as the button's tooltip
    /// and read back as its automation help text.</summary>
    public static readonly DependencyProperty CloseHintProperty =
        DependencyProperty.Register(nameof(CloseHint), typeof(string), typeof(CaptionBar),
            new PropertyMetadata("Close"));

    public CaptionBar() => InitializeComponent();

    public bool CanMinimise
    {
        get => (bool)GetValue(CanMinimiseProperty);
        set => SetValue(CanMinimiseProperty, value);
    }

    public string CloseHint
    {
        get => (string)GetValue(CloseHintProperty);
        set => SetValue(CloseHintProperty, value);
    }

    private void Minimise_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) SystemCommands.MinimizeWindow(window);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) SystemCommands.CloseWindow(window);
    }
}
