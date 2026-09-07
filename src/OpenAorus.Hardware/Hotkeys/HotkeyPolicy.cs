using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Hotkeys;

/// <summary>What the app should do about one signal.</summary>
public enum HotkeyOutcome
{
    /// <summary>Nothing. Either the app cannot service the key, or Windows already handles it.</summary>
    Ignore,

    /// <summary>Apply <see cref="HotkeyAction.Mode"/> through the existing fan controller.</summary>
    CycleFanMode,

    /// <summary>Move the lighting panel's brightness to <see cref="HotkeyAction.Level"/>. The
    /// firmware has already changed it; nothing is written back to the keyboard.</summary>
    SetBacklightLevel,

    /// <summary>Say so and no more: the firmware already performed the toggle.</summary>
    Notify,

    /// <summary>Move the panel brightness one step in <see cref="HotkeyAction.Step"/>.</summary>
    /// <remarks>THE ONLY OUTCOME WHOSE RESULT THE POLICY CANNOT PREDICT. Every other one names
    /// what will happen; this one asks the panel to move and does not know where it lands, because
    /// the levels a panel accepts are the panel's to say. So the caller finds out and words the
    /// card itself - see <see cref="HotkeyAction.Text"/>.</remarks>
    StepPanelBrightness,
}

/// <summary>One decision, in the terms the view model acts in.</summary>
/// <param name="Outcome">What to do.</param>
/// <param name="Mode">The fan mode to apply, for <see cref="HotkeyOutcome.CycleFanMode"/>.</param>
/// <param name="Level">The backlight level in percent, for <see cref="HotkeyOutcome.SetBacklightLevel"/>.</param>
/// <param name="Text">What the overlay would say. Empty for
/// <see cref="HotkeyOutcome.StepPanelBrightness"/>, which is the one card this class cannot word:
/// the number on it is the level the panel reports after the step, and this class has no panel.
/// The view model fills it in from what actually happened, and draws nothing if nothing did.</param>
/// <param name="ShowOverlay">Whether the owner asked to see this one.</param>
/// <param name="Step">Which way to move the panel brightness, for
/// <see cref="HotkeyOutcome.StepPanelBrightness"/>: 1 up, -1 down, 0 for every other outcome. A
/// direction rather than a level, and separate from <paramref name="Level"/> on purpose - "-1" read
/// as a percentage is a wrong number rather than a wrong field.</param>
public sealed record HotkeyAction(
    HotkeyOutcome Outcome,
    FanMode? Mode = null,
    int Level = 0,
    string Text = "",
    bool ShowOverlay = false,
    int Step = 0)
{
    /// <summary>Do nothing, say nothing, draw nothing.</summary>
    public static readonly HotkeyAction None = new(HotkeyOutcome.Ignore);
}

/// <summary>
/// Turns one decoded signal into one decision, given what the fans are doing and what the owner
/// switched on.
/// </summary>
/// <remarks>
/// <para>
/// The same division of labour as <see cref="FanSafety"/> and the fan watchdog: everything the
/// app does in response to an Fn key is decided in this one pure place, and
/// <c>MainViewModel</c> does the acting. Nothing here has a clock, touches the OS or knows what
/// an overlay looks like.
/// </para>
/// <para>
/// Two suppressions, and they are not the same kind of thing. <em>Volume</em> is structural: its
/// keys live on the standard consumer-control collection, which this app deliberately never
/// registers, so no volume signal exists in <see cref="HotkeySignal"/> and nothing here could
/// act on one. The 9-byte <em>display-brightness report</em> is a policy decision made in this
/// class: it arrives on <c>0xFF00/0xFF00</c>, which <em>is</em> registered, so it reaches
/// <see cref="Decide"/> and is refused there. Do not read the two as one guarantee - the
/// report's refusal is a rule this class keeps and could stop keeping, while a volume arm
/// cannot be deleted because there is none to delete.
/// </para>
/// <para>
/// AND ONE REVERSAL, WHICH IS NEITHER. <see cref="HotkeySignal.PanelBrightnessUp"/> and
/// <see cref="HotkeySignal.PanelBrightnessDown"/> are the brightness <em>keys</em>, not the
/// report, and they are serviced and drawn. The rule that suppressed everything brightness-shaped
/// was "do not draw a second card over one Windows already draws"; on this chassis the firmware
/// reports these keys and does not act on them, so nothing changes and nothing is drawn until this
/// app does both, and the change it makes never reaches Windows' hotkey path. There is no first
/// card to be second to. See <see cref="Config.HotkeySettings.OverlayForPanelBrightness"/> for the
/// argument in full, and note that the report above and these keys must not be collapsed into one
/// case: they mean opposite things about who changed the brightness.
/// </para>
/// <para>
/// That refusal is not something a settings file can reach. <see cref="Decide"/> builds the
/// action without an overlay flag and then sets it, at one exit, from
/// <see cref="MayDraw"/> - whose switch names the serviced signals and answers false to
/// everything else - and an ignored signal is flattened to <see cref="HotkeyAction.None"/>
/// before that. So making an unserviced signal draw takes two edits, in <see cref="Service"/>
/// and in <see cref="MayDraw"/>, and no combination of flags is one of them.
/// </para>
/// <para>
/// The <c>default</c> arm is deliberately the ignore arm. A signal added to
/// <see cref="HotkeySignal"/> later is inert until someone writes a case for it, which is the
/// safe direction for a decoder whose reports arrive regardless of focus.
/// </para>
/// </remarks>
public static class HotkeyPolicy
{
    /// <summary>Decides what one signal means right now.</summary>
    /// <param name="signal">The decoded signal.</param>
    /// <param name="current">The fan mode the app believes is in force.</param>
    /// <param name="settings">The owner's hotkey and overlay toggles.</param>
    /// <returns>The action to take; <see cref="HotkeyAction.None"/> for anything not serviced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signal"/> or
    /// <paramref name="settings"/> is null.</exception>
    public static HotkeyAction Decide(HotkeyEvent signal, FanMode current, HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled) return HotkeyAction.None;

        var action = Service(signal, current);

        // The single place an overlay is ever asked for. Everything that decided to do nothing
        // becomes the one canonical nothing first, so a signal the app does not service cannot
        // carry text - let alone a flag - into the view model.
        return action.Outcome == HotkeyOutcome.Ignore
            ? HotkeyAction.None
            : action with { ShowOverlay = MayDraw(signal.Signal, settings) };
    }

    /// <summary>The mode the fan key moves to from <paramref name="current"/>.</summary>
    /// <remarks>Fixed and Custom are not on the ring - the ring is the four automatic modes - and
    /// they land on Normal rather than Quiet, because an owner running Fixed at a high duty under
    /// load must not be dropped to the quietest mode the machine has by one keypress.</remarks>
    /// <param name="current">The mode in force.</param>
    /// <returns>The mode one press away.</returns>
    public static FanMode NextMode(FanMode current) => current switch
    {
        FanMode.Quiet => FanMode.Normal,
        FanMode.Normal => FanMode.Gaming,
        FanMode.Gaming => FanMode.Turbo,
        FanMode.Turbo => FanMode.Quiet,
        _ => FanMode.Normal,
    };

    /// <summary>
    /// The mode each fan wire code names in Gigabyte's own firmware, or null for anything else.
    /// </summary>
    /// <remarks>
    /// Deliberately unused. The codes name stealth, auto low and auto high, and if the firmware
    /// turns out to run its own rotation underneath the app's, honouring the named mode is the
    /// better behaviour and this is the table <see cref="Decide"/> would switch to - replacing
    /// <c>NextMode(current)</c> in the fan arm of <see cref="Service"/> with
    /// <c>NamedMode(signal.Signal) ?? current</c>. Keeping it here and tested is what stops the
    /// recovered information being thrown away, and the three codes stay three signals so there
    /// is something left to switch to. <c>VERIFY.md</c> 7.5 is the observation that decides.
    /// </remarks>
    /// <param name="signal">The decoded signal.</param>
    /// <returns>The named mode, or null if this signal names none.</returns>
    public static FanMode? NamedMode(HotkeySignal signal) => signal switch
    {
        HotkeySignal.FanModeStealth => FanMode.Quiet,
        HotkeySignal.FanModeAutoLow => FanMode.Normal,
        HotkeySignal.FanModeAutoHigh => FanMode.Gaming,
        _ => null,
    };

    /// <summary>What to do about a signal, before anyone asks whether to draw it.</summary>
    /// <remarks>No arm sets <see cref="HotkeyAction.ShowOverlay"/> and no arm reads
    /// <see cref="HotkeySettings"/>: doing what the key asks and saying so on screen are separate
    /// decisions, and keeping them in separate methods is what makes the second one provable.</remarks>
    private static HotkeyAction Service(HotkeyEvent signal, FanMode current)
    {
        switch (signal.Signal)
        {
            case HotkeySignal.FanModeStealth:
            case HotkeySignal.FanModeAutoLow:
            case HotkeySignal.FanModeAutoHigh:
            {
                // The spec asks for a cycle. NamedMode(signal.Signal) is the other reading,
                // kept beside this and one line away - see its remarks and VERIFY 7.5.
                var next = NextMode(current);
                return new HotkeyAction(HotkeyOutcome.CycleFanMode, next, 0, $"Fan mode: {next}");
            }

            case HotkeySignal.KeyboardBacklightLevel:
                return new HotkeyAction(
                    HotkeyOutcome.SetBacklightLevel, null, signal.Level, $"Keyboard backlight {signal.Level} %");

            case HotkeySignal.TouchpadEnabled:
                return new HotkeyAction(HotkeyOutcome.Notify, null, 0, "Touchpad on");
            case HotkeySignal.TouchpadDisabled:
                return new HotkeyAction(HotkeyOutcome.Notify, null, 0, "Touchpad off");
            case HotkeySignal.WifiEnabled:
                return new HotkeyAction(HotkeyOutcome.Notify, null, 0, "Wi-Fi on");
            case HotkeySignal.WifiDisabled:
                return new HotkeyAction(HotkeyOutcome.Notify, null, 0, "Wi-Fi off");

            case HotkeySignal.PanelBrightnessUp:
                // OBSERVED ON HARDWARE, AND THE FIRMWARE DOES NOT ACT ON IT. The key is reported
                // and the panel does not move, so this app has to move it - see IPanelBrightness.
                // A direction and nothing else: which level that lands on is the panel's to say,
                // and the text is left empty for the caller to fill in from what happened.
                return new HotkeyAction(HotkeyOutcome.StepPanelBrightness, null, 0, "", false, 1);

            case HotkeySignal.PanelBrightnessDown:
                return new HotkeyAction(HotkeyOutcome.StepPanelBrightness, null, 0, "", false, -1);

            case HotkeySignal.DisplayBrightness:
                // NOT THE TWO CASES ABOVE, and the difference is which of them says the brightness
                // has already changed. This one does - it is the 9-byte report, and something else
                // made the change and drew its own card for it - so a card from this app would be
                // the second, which is the complaint this release answers. The keys above say only
                // that a key was pressed, and nothing has happened yet.
                //
                // Documentary, not load-bearing: the default arm returns the same nothing, so
                // deleting this case changes no behaviour. What enforces the refusal is the Ignore
                // outcome itself, which Decide flattens to None before anyone asks about drawing.
                return HotkeyAction.None;

            default:
                // The launcher codes, the firmware reply, and anything a later version decodes
                // before anyone writes a case for it.
                return HotkeyAction.None;
        }
    }

    /// <summary>Whether the owner asked to see this signal on screen.</summary>
    /// <remarks>
    /// The only reader of the overlay toggles, and total: a signal with no case here cannot draw
    /// under any settings file, because there is no switch to find, in this class or in
    /// <see cref="HotkeySettings"/>. That is the second layer of the 9-byte brightness report's
    /// suppression, not the first - it never reaches this method at all, since
    /// <see cref="Service"/> classifies it as <see cref="HotkeyOutcome.Ignore"/> and
    /// <see cref="Decide"/> flattens Ignore to <see cref="HotkeyAction.None"/> before asking.
    /// Answering true for it here would change nothing on its own; the totality is what would
    /// catch it if that classification or that flattening ever went away.
    ///
    /// The two brightness keys DO have an arm, and it is a different setting from anything the
    /// report could reach. Adding an <c>OverlayForDisplayBrightness</c> case here would be the
    /// edit that undoes the release; adding this one was the edit that made the keys usable.
    /// </remarks>
    private static bool MayDraw(HotkeySignal signal, HotkeySettings settings) => signal switch
    {
        HotkeySignal.FanModeStealth or HotkeySignal.FanModeAutoLow or HotkeySignal.FanModeAutoHigh =>
            settings.OverlayForFanMode,
        HotkeySignal.KeyboardBacklightLevel => settings.OverlayForBacklight,
        HotkeySignal.TouchpadEnabled or HotkeySignal.TouchpadDisabled => settings.OverlayForTouchpad,
        HotkeySignal.WifiEnabled or HotkeySignal.WifiDisabled => settings.OverlayForWifi,
        HotkeySignal.PanelBrightnessUp or HotkeySignal.PanelBrightnessDown =>
            settings.OverlayForPanelBrightness,
        _ => false,
    };
}
