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
}

/// <summary>One decision, in the terms the view model acts in.</summary>
/// <param name="Outcome">What to do.</param>
/// <param name="Mode">The fan mode to apply, for <see cref="HotkeyOutcome.CycleFanMode"/>.</param>
/// <param name="Level">The backlight level in percent, for <see cref="HotkeyOutcome.SetBacklightLevel"/>.</param>
/// <param name="Text">What the overlay would say.</param>
/// <param name="ShowOverlay">Whether the owner asked to see this one.</param>
public sealed record HotkeyAction(
    HotkeyOutcome Outcome,
    FanMode? Mode = null,
    int Level = 0,
    string Text = "",
    bool ShowOverlay = false)
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
/// act on one. <em>Display brightness</em> is a policy decision made in this class: it arrives on
/// <c>0xFF00/0xFF00</c>, which <em>is</em> registered, so it reaches
/// <see cref="Decide"/> and is refused there. Do not read the two as one guarantee - deleting
/// the brightness arm would bring the duplicate overlay straight back, while deleting a volume
/// arm is impossible because there is none.
/// </para>
/// <para>
/// That refusal is not something a settings file can reach. <see cref="Decide"/> builds the
/// action without an overlay flag and then sets it, at one exit, from
/// <see cref="MayDraw"/> - whose switch names the serviced signals and answers false to
/// everything else - and an ignored signal is flattened to <see cref="HotkeyAction.None"/>
/// before that. So the only way to make an unserviced signal draw is to add a case to
/// <see cref="MayDraw"/>, not to set a flag.
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
    /// is something left to switch to. <c>VERIFY.md</c> 8.5 is the observation that decides.
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
                // kept beside this and one line away - see its remarks and VERIFY 8.5.
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

            case HotkeySignal.DisplayBrightness:
                // Named rather than left to the default arm, because this one is a choice. The
                // panel brightness really does arrive here, Windows really does already draw for
                // it, and a second card is the complaint this release answers. Unlike volume,
                // nothing structural is stopping it - only this line.
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
    /// under any settings file. That is what makes the brightness suppression un-configurable
    /// rather than merely off by default - there is no switch to find, in this class or in
    /// <see cref="HotkeySettings"/>.
    /// </remarks>
    private static bool MayDraw(HotkeySignal signal, HotkeySettings settings) => signal switch
    {
        HotkeySignal.FanModeStealth or HotkeySignal.FanModeAutoLow or HotkeySignal.FanModeAutoHigh =>
            settings.OverlayForFanMode,
        HotkeySignal.KeyboardBacklightLevel => settings.OverlayForBacklight,
        HotkeySignal.TouchpadEnabled or HotkeySignal.TouchpadDisabled => settings.OverlayForTouchpad,
        HotkeySignal.WifiEnabled or HotkeySignal.WifiDisabled => settings.OverlayForWifi,
        _ => false,
    };
}
