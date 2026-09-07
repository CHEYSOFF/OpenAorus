namespace OpenAorus.Hardware.Hotkeys;

/// <summary>Everything the two channels can say that this app understands.</summary>
/// <remarks>
/// <para>
/// Wider than the set the app acts on, on purpose. A signal that is decoded and then ignored -
/// display brightness, the three launcher codes, the firmware version reply - is one that cannot
/// be mistaken for something else by a looser pattern later. Volume is absent because it never
/// arrives: it lives on the consumer-control collection, which this app deliberately never
/// registers.
/// </para>
/// <para>
/// TWO PROVENANCES IN ONE ENUM, and the difference matters when one of these turns out to be
/// wrong. Every member below is recovered from Gigabyte's decompiled binaries and has never been
/// seen arrive, EXCEPT <see cref="PanelBrightnessDown"/> and <see cref="PanelBrightnessUp"/>,
/// which were watched on an AORUS 17G KD through the diagnostics trace and are marked as such on
/// their own documentation. A decompiled member that does nothing on the bench is a research gap
/// to be investigated; an observed one that does nothing is a regression here.
/// </para>
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
    /// <remarks>
    /// NOT the two keys below, and the difference is the whole reason all three exist. This is the
    /// decompiled 9-byte report - <c>9, _, 1, 3</c> with a level in byte 6 - which says the panel
    /// brightness <em>has already changed</em>. The 17G KD has never been seen to send it, but it
    /// is kept because another chassis may, and on a chassis that sends it Windows is the one
    /// making the change and drawing for it. So this stays refused, permanently, while
    /// <see cref="PanelBrightnessUp"/> and <see cref="PanelBrightnessDown"/> are serviced.
    /// </remarks>
    DisplayBrightness,

    /// <summary>OBSERVED ON HARDWARE. The brightness-down key was pressed. Wire code 125,
    /// <c>04 00 00 7D</c> on the 4-byte collection.</summary>
    /// <remarks>
    /// <para>
    /// One of the two members of this enum that is a measurement rather than a reading of
    /// decompiled code; see the enum's own remarks. Watched on an AORUS 17G KD, 2026-09-07, and
    /// written down in <c>docs/research/fn-hotkey-signals.md</c> under "Observed on hardware".
    /// </para>
    /// <para>
    /// An intent, not a report of something that happened. The firmware sends this and then does
    /// nothing: the panel does not dim. That is the finding that makes this signal serviced rather
    /// than ignored - the app has to perform the change itself, through
    /// <see cref="Display.PanelBrightnessController"/>, and Windows draws nothing because the
    /// change never travels through its hotkey path.
    /// </para>
    /// </remarks>
    PanelBrightnessDown,

    /// <summary>OBSERVED ON HARDWARE. The brightness-up key was pressed. Wire code 126,
    /// <c>04 00 00 7E</c> on the 4-byte collection.</summary>
    /// <remarks>The other measured member; see <see cref="PanelBrightnessDown"/> for what that
    /// means and why an intent the firmware never carries out has to be serviced here.</remarks>
    PanelBrightnessUp,

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
