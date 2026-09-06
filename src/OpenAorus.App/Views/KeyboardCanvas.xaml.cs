using OpenAorus.App.ViewModels;
using FrameworkElement = System.Windows.FrameworkElement;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;

namespace OpenAorus.App.Views;

/// <summary>
/// The picture of the keyboard that the per-key colours are painted on.
/// </summary>
/// <remarks>
/// The only thing here is pointer plumbing: WPF reports a press, a move, a key press and a
/// release, and <see cref="PerKeyEditorViewModel"/> decides what each of them means. Nothing about
/// which key or which slot is settled in this file, because none of it would then be reachable by
/// a test.
/// </remarks>
public partial class KeyboardCanvas : UserControl
{
    public KeyboardCanvas() => InitializeComponent();

    private PerKeyEditorViewModel? Editor => DataContext as PerKeyEditorViewModel;

    private static KeySlotViewModel? KeyOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as KeySlotViewModel;

    /// <summary>
    /// The key drawn under a point on the picture, or null for the gap between two. Walks up from
    /// whatever was hit because the caption sits in its own TextBlock on top of the key.
    /// </summary>
    private KeySlotViewModel? KeyUnder(Point point)
    {
        var hit = VisualTreeHelper.HitTest(PaintSurface, point);
        for (var node = hit?.VisualHit; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement element && element.DataContext is KeySlotViewModel key)
                return key;
        return null;
    }

    private void Key_Down(object sender, MouseButtonEventArgs e)
    {
        if (Editor is not { } editor) return;
        editor.BeginPaint();
        editor.PaintCommand.Execute(KeyOf(sender));
        // Held for the length of the stroke so the release is still ours wherever it happens.
        // A capture also stops MouseEnter reaching the individual keys, which is why the rest of
        // the stroke is resolved by hit-testing the reported positions instead.
        PaintSurface.CaptureMouse();
    }

    /// <summary>
    /// Space and Enter paint the focused key. Without this the picture is reachable by tab and
    /// then does nothing, which is worse than not being reachable at all.
    /// </summary>
    private void Key_Pressed(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;
        Editor?.PaintCommand.Execute(KeyOf(sender));
        e.Handled = true;
    }

    /// <summary>Carries a drag across the keys the pointer passes over.</summary>
    private void Paint_Move(object sender, MouseEventArgs e)
    {
        if (Editor is not { IsPainting: true } editor) return;
        editor.PaintOver(KeyUnder(e.GetPosition(PaintSurface)));
    }

    private void Paint_Up(object sender, MouseButtonEventArgs e) => EndStroke();

    /// <summary>Leaving the picture ends the stroke: without this a drag that wanders off the
    /// keys and back again would carry on painting long after the button came up. Only reachable
    /// when the capture was refused, since a held capture keeps the pointer nominally inside.</summary>
    private void Paint_Left(object sender, MouseEventArgs e) => EndStroke();

    /// <summary>The capture taken away by something else - a menu, a drag-drop - ends the stroke
    /// too, or the next hover over the picture would paint with no button held.</summary>
    private void Paint_Lost(object sender, MouseEventArgs e) => Editor?.EndPaint();

    private void EndStroke()
    {
        Editor?.EndPaint();
        if (PaintSurface.IsMouseCaptured) PaintSurface.ReleaseMouseCapture();
    }
}
