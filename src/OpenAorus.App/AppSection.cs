namespace OpenAorus.App;

/// <summary>The two halves of the window, chosen with the switch above the content.</summary>
public enum AppSection
{
    /// <summary>Fan modes, sensors, the curve editor and the charge limit - all of v0.1.</summary>
    Cooling,

    /// <summary>Keyboard lighting: effect, parameters, presets and the per-key editor.</summary>
    Lighting,
}

/// <summary>
/// The sections as fields, so XAML can name one with <c>x:Static</c> as a command parameter.
/// Same shape and same reason as <see cref="FanModes"/>.
/// </summary>
public static class AppSections
{
    /// <summary><see cref="AppSection.Cooling"/>.</summary>
    public static readonly AppSection Cooling = AppSection.Cooling;

    /// <summary><see cref="AppSection.Lighting"/>.</summary>
    public static readonly AppSection Lighting = AppSection.Lighting;
}
