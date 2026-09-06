using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OpenAorus.App.ViewModels;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OpenAorus.App.Views;

public partial class ContextPanel : UserControl
{
    public ContextPanel()
    {
        InitializeComponent();
        Editor.Apply.Click += async (_, _) =>
        {
            if (DataContext is MainViewModel vm) await vm.ApplyCurveCommand.ExecuteAsync(null);
        };
    }

    private async void FixedSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.ApplyFixedCommand.ExecuteAsync(null);
    }

    private async void FixedSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End
            && DataContext is MainViewModel vm)
        {
            await vm.ApplyFixedCommand.ExecuteAsync(null);
        }
    }
}
