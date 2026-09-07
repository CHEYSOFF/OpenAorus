namespace OpenAorus.Hardware.Config;

/// <summary>
/// The hotkey half of <see cref="AppSettings"/>: whether to listen at all, and which signals the
/// owner wants to see an overlay for.
/// </summary>
/// <remarks>
/// <para>
/// Listening defaults on and every overlay defaults off, with one argued-for exception in
/// <see cref="OverlayForPanelBrightness"/>. That split is the whole design: making the Fn row work
/// is the feature, and an on-screen display nobody asked for is the bug being fixed.
/// </para>
/// <para>
/// There is deliberately no toggle for volume, and adding one would be a mistake rather than a
/// feature: the volume keys live on the consumer-control collection this app never registers, so
/// they never arrive, there is no signal for them, and <see cref="Hotkeys.HotkeyPolicy"/> could not
/// act on one if there were. A switch would promise something the app cannot do.
/// </para>
/// <para>
/// There is likewise no toggle for the 9-byte display-brightness <em>report</em>, which is a
/// different thing from the two brightness keys - see <see cref="OverlayForPanelBrightness"/>. That
/// report says the brightness has already changed, which means something else changed it and that
/// something drew its own card; <see cref="Hotkeys.HotkeyPolicy"/> refuses it whatever is set here.
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

    /// <summary>Show the overlay when a brightness key moves the panel. THE ONE THAT DEFAULTS ON.</summary>
    /// <remarks>
    /// <para>
    /// THIS IS A DELIBERATE REVERSAL OF THE RULE THE REST OF THIS CLASS KEEPS, and it is worth
    /// setting out why, because "never draw for brightness" was written down as a principle and
    /// this looks like it being abandoned.
    /// </para>
    /// <para>
    /// The rule's purpose was never "brightness is special". It was <em>do not draw a second card
    /// over one Windows already draws</em> - Gigabyte's software layered its own OSD on top of the
    /// system's, and one keypress produced two cards. That is the complaint this release answers.
    /// </para>
    /// <para>
    /// On this chassis Windows draws nothing for these two keys. The firmware reports them and does
    /// not act on them, so no brightness change happens at all until this app makes one, and the
    /// change it makes goes through <c>WmiSetBrightness</c> rather than through Windows' hotkey
    /// path - so Windows does not narrate it either. There is no first card to be second to. The
    /// rule's purpose now argues for drawing rather than against it, and following the letter of it
    /// would leave the owner pressing a key with no feedback but the screen itself.
    /// </para>
    /// <para>
    /// It defaults on for that reason and no other: every other signal here has feedback of its own
    /// - the mode buttons move, the backlight changes under the owner's hands, Windows narrates the
    /// radio - so their cards are a convenience and start off. This one is the only feedback there
    /// is, so it starts on and can be turned off by anyone who does not want it. The switch still
    /// governs only the drawing; the keys work either way.
    /// </para>
    /// </remarks>
    public bool OverlayForPanelBrightness { get; set; } = true;

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
    /// are clamped rather than reset. The five overlay toggles are booleans with no out-of-range
    /// state to find, and <see cref="Enabled"/> is likewise, so "repairing" any of them could only
    /// mean quietly undoing a choice the owner made.
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
