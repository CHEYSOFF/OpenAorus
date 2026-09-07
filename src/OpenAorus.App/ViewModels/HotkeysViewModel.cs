using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.Config;

namespace OpenAorus.App.ViewModels;

/// <summary>
/// The Settings card for the Fn row: whether to listen, which signals draw a card, and for how
/// long.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no switch for volume, and adding one would be a mistake rather than an
/// omission to fix. The volume keys never reach this app at all - they live on the consumer-control
/// collection it never registers - so a switch could only promise something the app cannot do.
/// There is likewise none for the 9-byte display-brightness report, which says the brightness has
/// already changed and therefore that something else changed it and drew its own card;
/// <see cref="Hardware.Hotkeys.HotkeyPolicy"/> refuses that whatever is set here.
/// </para>
/// <para>
/// <see cref="OverlayForPanelBrightness"/> is neither of those and is the one switch that starts
/// ticked. It governs the two Fn keys the firmware reports and does not act on; nothing else on
/// the machine draws for them, because the change this app makes instead never travels through
/// Windows' hotkey path. See <see cref="HotkeySettings.OverlayForPanelBrightness"/>.
/// </para>
/// <para>
/// The card sits outside the Settings window's <c>CanWrite</c> gate. Three of the four halves -
/// touchpad, Wi-Fi and backlight - need no WMI at all, so disabling them on a model the app does
/// not recognise would take away working features to protect the one that does not work. Only fan
/// cycling is affected there, and the fan controller already refuses it with a message of its own.
/// </para>
/// <para>
/// <see cref="Save"/> writes into the live <see cref="HotkeySettings"/> instance rather than a
/// copy of it, because that instance is the one <see cref="Hotkeys.HotkeyService"/> was handed:
/// unticking an overlay takes effect on the next keypress, not on the next launch. Only
/// <see cref="HotkeysEnabled"/> is different, and only in one direction - see its remarks.
/// </para>
/// </remarks>
public sealed partial class HotkeysViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Action<string> _report;

    /// <summary>Whether the app listens to the Fn row at all.</summary>
    /// <remarks>Named for the binding rather than for the field it saves to: this view model is
    /// the Settings window's data context for one card, where a bare "Enabled" would read as the
    /// card's own state.</remarks>
    [ObservableProperty] private bool _hotkeysEnabled;

    [ObservableProperty] private bool _overlayForFanMode;
    [ObservableProperty] private bool _overlayForBacklight;
    [ObservableProperty] private bool _overlayForTouchpad;
    [ObservableProperty] private bool _overlayForWifi;

    /// <summary>The one switch on this card that starts ticked.</summary>
    /// <remarks>See <see cref="HotkeySettings.OverlayForPanelBrightness"/> for the argument. In
    /// short: the two brightness keys are the only ones with no feedback behind them at all, so
    /// their card is not a convenience but the whole of what the owner sees happen.</remarks>
    [ObservableProperty] private bool _overlayForPanelBrightness;

    [ObservableProperty] private int _overlaySeconds;

    /// <param name="s">The app's services; its <see cref="AppSettings.Hotkeys"/> is the live
    /// settings object the hotkey service reads.</param>
    /// <param name="report">Where to say what happened. The Settings window has one status line,
    /// at the foot of the dialog, and this card speaks through it rather than growing a second
    /// one of its own - the same line the sensor-polling Save already writes to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> or <paramref name="report"/>
    /// is null.</exception>
    public HotkeysViewModel(AppServices s, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(report);
        _s = s;
        _report = report;

        var saved = s.Settings.Hotkeys;
        _hotkeysEnabled = saved.Enabled;
        _overlayForFanMode = saved.OverlayForFanMode;
        _overlayForBacklight = saved.OverlayForBacklight;
        _overlayForTouchpad = saved.OverlayForTouchpad;
        _overlayForWifi = saved.OverlayForWifi;
        _overlayForPanelBrightness = saved.OverlayForPanelBrightness;
        _overlaySeconds = saved.OverlaySeconds;
    }

    [RelayCommand]
    private void Save()
    {
        var requested = OverlaySeconds;
        var saved = _s.Settings.Hotkeys;
        saved.Enabled = HotkeysEnabled;
        saved.OverlayForFanMode = OverlayForFanMode;
        saved.OverlayForBacklight = OverlayForBacklight;
        saved.OverlayForTouchpad = OverlayForTouchpad;
        saved.OverlayForWifi = OverlayForWifi;
        saved.OverlayForPanelBrightness = OverlayForPanelBrightness;
        saved.OverlaySeconds = Math.Clamp(
            requested, HotkeySettings.MinOverlaySeconds, HotkeySettings.MaxOverlaySeconds);

        // Pushed back into the box the owner typed into, the way the poll intervals are: a card
        // still reading 900 while the app uses 10 would be the settings lying about themselves.
        OverlaySeconds = saved.OverlaySeconds;

        _s.Store.Save(_s.Settings);

        var notes = new List<string> { "Hotkey settings saved." };

        // Said rather than done silently. The number in the box changes under the owner's hands,
        // and a correction nobody explains reads as the app losing what was typed.
        if (saved.OverlaySeconds != requested)
            notes.Add($"The overlay duration was brought inside {HotkeySettings.MinOverlaySeconds}-" +
                      $"{HotkeySettings.MaxOverlaySeconds} seconds.");

        // The channels are opened once, in AppServices.Create, and only if the file already asked
        // for them - so switching listening on mid-run changes the file and nothing else. Off is
        // not symmetrical: HotkeyPolicy.Decide reads this same live flag and stops every signal
        // dead whether or not the channels are open, so it needs no restart and gets no notice.
        if (HotkeysEnabled && _s.Hotkeys is null)
            notes.Add("Restart OpenAorus to start listening - the Fn channels are opened once, at startup.");

        _report(string.Join(" ", notes));
    }
}
