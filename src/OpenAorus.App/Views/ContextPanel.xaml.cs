using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenAorus.App.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

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
}
