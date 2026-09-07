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
/// <para>
/// Like the lighting and fan settings, everything here arrives from a file on disk that anyone
/// can edit and that older or newer versions of the app may have written, so <see cref="Repair"/>
/// exists to bring a loaded instance back inside the range the app accepts.
/// <see cref="SettingsStore.Load"/> calls it; nothing else needs to.
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

    /// <summary>
    /// Replaces any value that could not have come from this app with its nearest legal one, and
    /// reports whether it had to.
    /// </summary>
    /// <remarks>
    /// The same job and the same call site as <see cref="LightingSettings.Repair"/> and
    /// <see cref="AppSettings.RepairFans"/>: one choke point in <see cref="SettingsStore.Load"/>,
    /// one notice, no second mechanism with its own way of speaking up.
    ///
    /// Only the duration is repairable, and that is the whole of it. A saved zero or a negative
    /// would put a card on screen that never comes down, and a saved thousand would do the same
    /// for long enough to be indistinguishable; both have an obvious nearest legal value, so both
    /// are clamped rather than reset. The four toggles are booleans with no out-of-range state to
    /// find, and <see cref="Enabled"/> is likewise, so "repairing" any of them could only mean
    /// quietly undoing a choice the owner made.
    ///
    /// The return value drives the notice that tells the owner, so this stays honest about having
    /// changed nothing.
    /// </remarks>
    /// <returns>True if any value was replaced.</returns>
    public bool Repair()
    {
        var clamped = Math.Clamp(OverlaySeconds, MinOverlaySeconds, MaxOverlaySeconds);
        if (clamped == OverlaySeconds) return false;

        OverlaySeconds = clamped;
        return true;
    }
}
