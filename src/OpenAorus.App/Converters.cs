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

/// <summary>
/// The outline of a key in the per-key editor: accent for the key painted last, the card border
/// for the rest. A converter rather than a DataTrigger so the two states are the only two there
/// are, whatever the binding hands over.
/// </summary>
public sealed class SelectionBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value is true
            ? System.Windows.Application.Current.FindResource("Accent")
            : System.Windows.Application.Current.FindResource("CardBorder");
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// Key captions in the per-key editor sit on top of the colour the key will light, so the text
/// has to invert with it rather than being one fixed colour that vanishes half the time.
/// </summary>
public sealed class ReadableForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush OnDark = Frozen(0xE6, 0xE8, 0xEC);
    private static readonly SolidColorBrush OnLight = Frozen(0x15, 0x17, 0x1B);

    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value is System.Windows.Media.Color color && !ColorText.IsDark(color) ? OnLight : OnDark;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => System.Windows.Data.Binding.DoNothing;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
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
