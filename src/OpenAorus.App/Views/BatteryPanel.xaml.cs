using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenAorus.App.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace OpenAorus.App.Views;

public partial class BatteryPanel : UserControl
{
    public BatteryPanel() => InitializeComponent();

    private async void Limit_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && DataContext is BatteryViewModel vm) await vm.ApplyCommand.ExecuteAsync(null);
    }

    private async void Stop_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is BatteryViewModel vm) await vm.ApplyCommand.ExecuteAsync(null);
    }
}
