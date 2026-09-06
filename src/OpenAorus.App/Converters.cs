using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OpenAorus.Hardware.Ui;

namespace OpenAorus.App;

/// <summary>
/// Button.Tag ⇐ "Selected" / "Unselected" for (SelectedMode == parameter); clicks go through the command.
/// Returns a string, not a bool: Tag is typed object, and a DataTrigger comparing an object-typed
/// value against Value="True" compares a string to a boxed bool and never fires.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public const string Selected = "Selected";
    public const string Unselected = "Unselected";

    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value is not null && parameter is not null && value.ToString() == parameter.ToString() ? Selected : Unselected;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => System.Windows.Data.Binding.DoNothing;
}

public sealed class BannerBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) => value switch
    {
        BannerKind.Warning => System.Windows.Application.Current.FindResource("Warn"),
        BannerKind.Error => System.Windows.Application.Current.FindResource("Err"),
        BannerKind.Info => System.Windows.Application.Current.FindResource("Info"),
        _ => System.Windows.Media.Brushes.Transparent,
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => System.Windows.Data.Binding.DoNothing;
}

public sealed class BannerVisibleConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value is BannerKind k && k != BannerKind.None ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => System.Windows.Data.Binding.DoNothing;
}

public sealed class EnumEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => System.Windows.Data.Binding.DoNothing;
}

public static class FanModes
{
    public static readonly Hardware.Fans.FanMode Quiet = Hardware.Fans.FanMode.Quiet;
    public static readonly Hardware.Fans.FanMode Normal = Hardware.Fans.FanMode.Normal;
    public static readonly Hardware.Fans.FanMode Gaming = Hardware.Fans.FanMode.Gaming;
    public static readonly Hardware.Fans.FanMode Turbo = Hardware.Fans.FanMode.Turbo;
    public static readonly Hardware.Fans.FanMode Fixed = Hardware.Fans.FanMode.Fixed;
    public static readonly Hardware.Fans.FanMode Custom = Hardware.Fans.FanMode.Custom;
}
