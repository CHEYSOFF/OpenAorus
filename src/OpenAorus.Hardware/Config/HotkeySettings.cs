namespace OpenAorus.Hardware.Config;

/// <summary>
/// The hotkey half of <see cref="AppSettings"/>: whether to listen at all, and which signals the
/// owner wants to see an overlay for.
/// </summary>
/// <remarks>
/// <para>
/// Listening defaults on and every overlay defaults off. That split is the whole design: making
/// the Fn row work is the feature, and an on-screen display nobody asked for is the bug being
/// fixed.
/// </para>
/// <para>
/// There is deliberately no toggle for volume or display brightness, and adding one would be a
/// mistake rather than a feature: Windows already draws both of those, and
/// <see cref="Hotkeys.HotkeyPolicy"/> refuses them whatever is set here. A switch would promise
/// something the policy will not do.
/// </para>
/// </remarks>
public sealed class HotkeySettings
{
    /// <summary>The shortest an overlay may stay up, in seconds.</summary>
    public const int MinOverlaySeconds = 1;

    /// <summary>The longest an overlay may stay up, in seconds.</summary>
    /// <remarks>A card that outlasts this stops reading as a notification and starts reading as
    /// a window that will not go away - which is the failure mode being replaced.</remarks>
    public const int MaxOverlaySeconds = 10;

    /// <summary>Whether the app listens to the two hotkey channels at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Show the overlay when the fan-mode key cycles the mode.</summary>
    public bool OverlayForFanMode { get; set; }

    /// <summary>Show the overlay when the keyboard changes its own backlight level.</summary>
    public bool OverlayForBacklight { get; set; }

    /// <summary>Show the overlay when the firmware toggles the touchpad.</summary>
    public bool OverlayForTouchpad { get; set; }

    /// <summary>Show the overlay when the firmware toggles the radio.</summary>
    public bool OverlayForWifi { get; set; }

    /// <summary>How long the overlay stays up, in seconds.</summary>
    public int OverlaySeconds { get; set; } = MinOverlaySeconds;
}
