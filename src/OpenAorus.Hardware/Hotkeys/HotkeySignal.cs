namespace OpenAorus.Hardware.Hotkeys;

/// <summary>Everything the two channels can say that this app understands.</summary>
/// <remarks>
/// Wider than the set the app acts on, on purpose. A signal that is decoded and then ignored -
/// display brightness, the three launcher codes, the firmware version reply - is one that cannot
/// be mistaken for something else by a looser pattern later. Volume is absent because it never
/// arrives: it lives on the consumer-control collection, which this app deliberately never
/// registers.
/// </remarks>
public enum HotkeySignal
{
    /// <summary>Wire code 37. Gigabyte's firmware calls this mode "stealth".</summary>
    FanModeStealth,

    /// <summary>Wire code 38. Gigabyte's firmware calls this mode "auto low".</summary>
    FanModeAutoLow,

    /// <summary>Wire code 39. Gigabyte's firmware calls this mode "auto high".</summary>
    FanModeAutoHigh,

    /// <summary>The keyboard changed its own backlight; <see cref="HotkeyEvent.Level"/> is the
    /// new level in percent.</summary>
    KeyboardBacklightLevel,

    /// <summary>The panel brightness changed. Decoded, then deliberately ignored: Windows draws
    /// this overlay already, and drawing a second one is the bug v0.3 exists to fix.</summary>
    DisplayBrightness,

    /// <summary>The firmware turned the touchpad on. Arrives on the WMI channel, not raw input.</summary>
    TouchpadEnabled,

    /// <summary>The firmware turned the touchpad off. Arrives on the WMI channel, not raw input.</summary>
    TouchpadDisabled,

    /// <summary>The firmware turned Wi-Fi on. Arrives on the WMI channel, not raw input.</summary>
    WifiEnabled,

    /// <summary>The firmware turned Wi-Fi off. Arrives on the WMI channel, not raw input.</summary>
    WifiDisabled,

    /// <summary>Gigabyte's recovery launcher code. Recognised so it cannot be misread; never acted on.</summary>
    LaunchRecovery,

    /// <summary>Gigabyte's update-all launcher code. Recognised so it cannot be misread; never acted on.</summary>
    LaunchUpdateAll,

    /// <summary>Gigabyte's update-all-with-defaults launcher code. Recognised so it cannot be
    /// misread; never acted on.</summary>
    LaunchUpdateAllDefault,

    /// <summary>A reply to a firmware-version request, not a keypress.</summary>
    FirmwareVersionReply,
}

/// <summary>One decoded signal, with the value that came with it where there was one.</summary>
/// <param name="Signal">What the keyboard or the firmware said.</param>
/// <param name="Level">Backlight level in percent, or the panel brightness byte. 0 otherwise.</param>
public sealed record HotkeyEvent(HotkeySignal Signal, int Level = 0);
