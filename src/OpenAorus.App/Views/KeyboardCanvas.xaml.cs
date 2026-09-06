using OpenAorus.App.ViewModels;
using FrameworkElement = System.Windows.FrameworkElement;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace OpenAorus.App.Views;

/// <summary>
/// The picture of the keyboard that the per-key colours are painted on.
/// </summary>
/// <remarks>
/// The only thing here is pointer plumbing: WPF reports a press, a hover and a release, and
/// <see cref="PerKeyEditorViewModel"/> decides what each of them means. Nothing about which key
/// or which slot is settled in this file, because none of it would then be reachable by a test.
/// </remarks>
public partial class KeyboardCanvas : UserControl
{
    public KeyboardCanvas() => InitializeComponent();

    private PerKeyEditorViewModel? Editor => DataContext as PerKeyEditorViewModel;

    private static KeySlotViewModel? KeyOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as KeySlotViewModel;

    private void Key_Down(object sender, MouseButtonEventArgs e)
    {
        if (Editor is not { } editor) return;
        editor.BeginPaint();
        editor.PaintCommand.Execute(KeyOf(sender));
    }

    private void Key_Enter(object sender, MouseEventArgs e) => Editor?.PaintOver(KeyOf(sender));

    private void Paint_Up(object sender, MouseButtonEventArgs e) => Editor?.EndPaint();

    /// <summary>Leaving the picture ends the stroke: without this a drag that wanders off the
    /// keys and back again would carry on painting long after the button came up.</summary>
    private void Paint_Left(object sender, MouseEventArgs e) => Editor?.EndPaint();
}
