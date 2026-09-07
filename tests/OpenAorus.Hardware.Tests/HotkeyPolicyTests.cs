using System.Reflection;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// What one decoded signal means, given what the fans are doing and what the owner turned on.
/// </summary>
/// <remarks>
/// The same shape as <see cref="OpenAorus.App.ViewModels.FanWatchdog"/>: a pure object that
/// decides, with the acting left to the view model. Everything the app does in response to an
/// Fn key is decided here, so everything is checkable here.
/// </remarks>
public class HotkeyPolicyTests
{
    private static HotkeySettings AllOverlaysOn() => new()
    {
        Enabled = true,
        OverlayForFanMode = true,
        OverlayForBacklight = true,
        OverlayForTouchpad = true,
        OverlayForWifi = true,
        OverlayForPanelBrightness = true,
    };

    /// <summary>The two signals this app services by changing the panel brightness itself.</summary>
    private static readonly HotkeySignal[] BrightnessKeys =
    {
        HotkeySignal.PanelBrightnessUp, HotkeySignal.PanelBrightnessDown,
    };

    /// <summary>Every signal the app does something about.</summary>
    private static readonly HotkeySignal[] Serviced =
    {
        HotkeySignal.FanModeStealth, HotkeySignal.FanModeAutoLow, HotkeySignal.FanModeAutoHigh,
        HotkeySignal.KeyboardBacklightLevel,
        HotkeySignal.TouchpadEnabled, HotkeySignal.TouchpadDisabled,
        HotkeySignal.WifiEnabled, HotkeySignal.WifiDisabled,
        HotkeySignal.PanelBrightnessUp, HotkeySignal.PanelBrightnessDown,
    };

    /// <summary>Every combination of the six switches an owner can throw, in a fixed order.</summary>
    /// <remarks>Six booleans is sixty-four settings files, which is small enough to walk
    /// exhaustively - and walking them is the only way to say "no setting can do this" rather
    /// than "the settings I thought of cannot do this". The six names and the bound are written
    /// out by hand, so <see cref="The_walk_still_covers_every_switch_there_is"/> is what keeps
    /// them honest as the settings grow. It was five and thirty-two until the panel-brightness
    /// overlay was added; that guard is what made the moment visible.</remarks>
    private static IEnumerable<HotkeySettings> EverySettingsCombination()
    {
        for (var bits = 0; bits < 64; bits++)
        {
            yield return new HotkeySettings
            {
                Enabled = (bits & 1) != 0,
                OverlayForFanMode = (bits & 2) != 0,
                OverlayForBacklight = (bits & 4) != 0,
                OverlayForTouchpad = (bits & 8) != 0,
                OverlayForWifi = (bits & 16) != 0,
                OverlayForPanelBrightness = (bits & 32) != 0,
            };
        }
    }

    /// <summary>
    /// The walk above still visits the whole space, and not half of it.
    /// </summary>
    /// <remarks>
    /// The strongest guarantee in this release - "no settings file can make the 9-byte display
    /// brightness report draw" - rests on a hand-written list of property names and a hand-written
    /// bound. A boolean added to <see cref="HotkeySettings"/> would leave that walk compiling and
    /// passing while covering half the settings files there are, with its own comment still
    /// claiming otherwise. Nothing else in the project would say a word.
    ///
    /// So the shape is asserted rather than assumed. Adding a switch fails here, loudly, and the
    /// failure names the walk that has to be extended: one more name in the list, one more bit,
    /// and the bound doubles.
    ///
    /// IT HAS ALREADY DONE ITS JOB ONCE. The list was five names and the bound 32 until
    /// <see cref="HotkeySettings.OverlayForPanelBrightness"/> arrived, at which point this failed
    /// and the walk was widened here on purpose rather than the guard being relaxed. That is what
    /// it is for: a change that quietly halves the coverage of every exhaustive test in this file
    /// has to be a change someone made deliberately.
    /// </remarks>
    [Fact]
    public void The_walk_still_covers_every_switch_there_is()
    {
        var switches = typeof(HotkeySettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool) && p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Enabled",
                "OverlayForBacklight",
                "OverlayForFanMode",
                "OverlayForPanelBrightness",
                "OverlayForTouchpad",
                "OverlayForWifi",
            },
            switches);

        // And the walk is sized to them rather than to the number that was true when it was
        // written: 2^6 files, each one distinct.
        var combinations = EverySettingsCombination().ToArray();
        Assert.Equal(1 << switches.Length, combinations.Length);
        Assert.Equal(
            combinations.Length,
            combinations
                .Select(s => (s.Enabled, s.OverlayForFanMode, s.OverlayForBacklight,
                              s.OverlayForTouchpad, s.OverlayForWifi, s.OverlayForPanelBrightness))
                .Distinct()
                .Count());
    }

    [Theory]
    [InlineData(FanMode.Quiet, FanMode.Normal)]
    [InlineData(FanMode.Normal, FanMode.Gaming)]
    [InlineData(FanMode.Gaming, FanMode.Turbo)]
    [InlineData(FanMode.Turbo, FanMode.Quiet)]
    public void The_fan_key_walks_the_four_automatic_modes_in_a_ring(FanMode current, FanMode expected)
    {
        Assert.Equal(expected, HotkeyPolicy.NextMode(current));
    }

    [Theory]
    [InlineData(FanMode.Fixed)]
    [InlineData(FanMode.Custom)]
    public void Off_the_ring_the_key_lands_on_Normal(FanMode current)
    {
        // Not Quiet. An owner on Fixed at 80 % under load who taps the key must not be dropped
        // to the quietest mode the machine has; Normal is the same one-line choice and is safe.
        Assert.Equal(FanMode.Normal, HotkeyPolicy.NextMode(current));
    }

    [Fact]
    public void Four_presses_come_back_to_where_they_started()
    {
        foreach (var start in new[] { FanMode.Quiet, FanMode.Normal, FanMode.Gaming, FanMode.Turbo })
        {
            var mode = start;
            var seen = new List<FanMode>();
            for (var i = 0; i < 4; i++)
            {
                mode = HotkeyPolicy.NextMode(mode);
                seen.Add(mode);
            }

            Assert.Equal(start, mode);
            Assert.Equal(4, seen.Distinct().Count());   // a ring, not a shorter loop
        }
    }

    [Theory]
    [InlineData(FanMode.Fixed)]
    [InlineData(FanMode.Custom)]
    public void From_off_the_ring_one_press_joins_it_and_the_next_three_close_it(FanMode start)
    {
        var first = HotkeyPolicy.NextMode(start);
        Assert.Equal(FanMode.Normal, first);

        Assert.Equal(FanMode.Gaming, HotkeyPolicy.NextMode(first));
        Assert.Equal(FanMode.Turbo, HotkeyPolicy.NextMode(FanMode.Gaming));
        Assert.Equal(FanMode.Quiet, HotkeyPolicy.NextMode(FanMode.Turbo));
        Assert.Equal(FanMode.Normal, HotkeyPolicy.NextMode(FanMode.Quiet));
    }

    [Fact]
    public void Every_fan_mode_has_a_next_one()
    {
        // Totality only - it fails only if NextMode returns an undefined value, and killed none
        // of six mutations to the ring. A_mode_this_app_has_never_defined... covers the property.
        foreach (var mode in Enum.GetValues<FanMode>())
            Assert.Contains(HotkeyPolicy.NextMode(mode), Enum.GetValues<FanMode>());
    }

    [Fact]
    public void A_mode_this_app_has_never_defined_still_lands_on_Normal()
    {
        // A settings file from a later version, deserialised into this one. Same reasoning as
        // Fixed and Custom: the unknown state is not on the ring, and the safe entry point is
        // the middle of it rather than the quiet end.
        Assert.Equal(FanMode.Normal, HotkeyPolicy.NextMode((FanMode)99));
    }

    [Theory]
    [InlineData(HotkeySignal.FanModeStealth)]
    [InlineData(HotkeySignal.FanModeAutoLow)]
    [InlineData(HotkeySignal.FanModeAutoHigh)]
    public void All_three_fan_codes_cycle_rather_than_selecting_the_mode_they_name(HotkeySignal signal)
    {
        var a = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, AllOverlaysOn());

        Assert.Equal(HotkeyOutcome.CycleFanMode, a.Outcome);
        Assert.Equal(FanMode.Gaming, a.Mode);
        Assert.True(a.ShowOverlay);
        Assert.Contains("Gaming", a.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HotkeySignal.FanModeStealth, FanMode.Quiet)]
    [InlineData(HotkeySignal.FanModeAutoLow, FanMode.Normal)]
    [InlineData(HotkeySignal.FanModeAutoHigh, FanMode.Gaming)]
    public void The_mode_each_code_names_is_kept_even_though_nothing_uses_it(HotkeySignal signal, FanMode named)
    {
        // The recovered information, preserved. If hardware shows the firmware runs its own
        // rotation underneath, Decide switches to this table and nothing else moves.
        Assert.Equal(named, HotkeyPolicy.NamedMode(signal));
    }

    [Fact]
    public void The_named_reading_and_the_cycling_one_are_genuinely_different_answers()
    {
        // The point of keeping both. If they agreed there would be nothing to decide on
        // hardware; they disagree on the very first press from the mode the app starts in.
        var cycled = HotkeyPolicy.Decide(
            new HotkeyEvent(HotkeySignal.FanModeStealth), FanMode.Normal, AllOverlaysOn()).Mode;

        Assert.Equal(FanMode.Gaming, cycled);
        Assert.Equal(FanMode.Quiet, HotkeyPolicy.NamedMode(HotkeySignal.FanModeStealth));
    }

    [Fact]
    public void Only_the_fan_codes_name_a_mode()
    {
        foreach (var signal in Enum.GetValues<HotkeySignal>())
        {
            var isFanCode = signal is HotkeySignal.FanModeStealth
                or HotkeySignal.FanModeAutoLow or HotkeySignal.FanModeAutoHigh;
            Assert.Equal(isFanCode, HotkeyPolicy.NamedMode(signal) is not null);
        }
    }

    [Theory]
    [InlineData(0, "Keyboard backlight 0 %")]
    [InlineData(50, "Keyboard backlight 50 %")]
    [InlineData(100, "Keyboard backlight 100 %")]
    public void The_backlight_signal_carries_its_level_through(int level, string expected)
    {
        var a = HotkeyPolicy.Decide(
            new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, level), FanMode.Normal, AllOverlaysOn());

        Assert.Equal(HotkeyOutcome.SetBacklightLevel, a.Outcome);
        Assert.Equal(level, a.Level);
        Assert.Null(a.Mode);
        // The level reaches the lighting panel as a number, but it reaches the owner as this
        // string. Pinning only Level leaves the half the owner actually reads unchecked.
        Assert.Equal(expected, a.Text);
    }

    [Theory]
    [InlineData(HotkeySignal.TouchpadEnabled, "Touchpad on")]
    [InlineData(HotkeySignal.TouchpadDisabled, "Touchpad off")]
    [InlineData(HotkeySignal.WifiEnabled, "Wi-Fi on")]
    [InlineData(HotkeySignal.WifiDisabled, "Wi-Fi off")]
    public void The_touchpad_and_radio_signals_are_display_only(HotkeySignal signal, string expected)
    {
        var a = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, AllOverlaysOn());

        // The firmware already did the toggle. There is nothing to write back.
        Assert.Equal(HotkeyOutcome.Notify, a.Outcome);
        // These four do nothing but say something, so the exact words are the whole behaviour:
        // an overlay reading "Wi-Fi off" as the radio comes up is this path being wrong, and
        // wrong silently. "Not empty" would not have noticed.
        Assert.Equal(expected, a.Text);
    }

    [Theory]
    [InlineData(HotkeySignal.DisplayBrightness)]
    [InlineData(HotkeySignal.LaunchRecovery)]
    [InlineData(HotkeySignal.LaunchUpdateAll)]
    [InlineData(HotkeySignal.LaunchUpdateAllDefault)]
    [InlineData(HotkeySignal.FirmwareVersionReply)]
    public void The_signals_the_app_cannot_service_do_nothing_at_all(HotkeySignal signal)
    {
        var a = HotkeyPolicy.Decide(new HotkeyEvent(signal, 42), FanMode.Normal, AllOverlaysOn());

        Assert.Equal(HotkeyOutcome.Ignore, a.Outcome);
        Assert.False(a.ShowOverlay);
    }

    [Fact]
    public void An_unknown_signal_is_a_no_op_rather_than_a_default_action()
    {
        // No decoder can produce this, but the enum is public and the decision must not fall
        // through to the fan cycle - which is what a switch with a wrong default would do.
        var a = HotkeyPolicy.Decide(new HotkeyEvent((HotkeySignal)999, 7), FanMode.Turbo, AllOverlaysOn());

        Assert.Equal(HotkeyAction.None, a);
    }

    [Fact]
    public void The_nine_byte_brightness_report_never_draws_an_overlay_whatever_is_switched_on()
    {
        // NOT the two brightness keys, and the distinction is the whole of this release's one
        // reversal. This signal is the decompiled 9-byte report saying the panel brightness has
        // ALREADY changed - which means something else changed it, and that something draws its
        // own card. The keys below are a different thing: an intent the firmware never carries
        // out, which this app performs itself and which Windows therefore never draws for.
        var a = HotkeyPolicy.Decide(
            new HotkeyEvent(HotkeySignal.DisplayBrightness, 80), FanMode.Turbo, AllOverlaysOn());

        Assert.False(a.ShowOverlay);
        Assert.Equal(HotkeyAction.None, a);
    }

    /// <summary>
    /// The rule that survived the reversal, stated over every settings file there is.
    /// </summary>
    /// <remarks>
    /// This replaces a test that said "no settings file can make brightness draw" full stop. That
    /// was written when the app drew for neither brightness key nor brightness report, and it is
    /// now wrong about the keys on purpose: the firmware reports them and does not act on them, so
    /// Windows draws nothing, so drawing is what the original rule - do not double-draw - now
    /// asks for. What did not change is the other two, and they are what this pins.
    ///
    /// Both halves in one test, because they are one rule seen from two sides. The 9-byte report
    /// is refused by a decision in <see cref="HotkeyPolicy"/> that could be un-made; volume cannot
    /// be refused because it was never received, and a Volume member appearing in the enum would
    /// mean something upstream had started registering the consumer-control collection.
    /// </remarks>
    [Fact]
    public void The_nine_byte_report_stays_undrawable_and_volume_stays_absent()
    {
        // Not "with the overlays I switched on" but "with every combination of switches there
        // is". The suppression has to be unreachable from the settings, not merely absent from
        // the defaults, because a second card over one Windows already draws is the complaint
        // this release exists to answer.
        foreach (var settings in EverySettingsCombination())
        foreach (var level in new[] { 0, 42, 80, 255 })
        {
            var a = HotkeyPolicy.Decide(
                new HotkeyEvent(HotkeySignal.DisplayBrightness, level), FanMode.Normal, settings);

            Assert.Equal(HotkeyAction.None, a);
        }

        // And the structural half: there is no volume signal to decide about, under any file.
        Assert.DoesNotContain(
            Enum.GetNames<HotkeySignal>(),
            name => name.Contains("Volume", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the two keys the firmware reports and does not act on ------------------------------

    [Theory]
    [InlineData(HotkeySignal.PanelBrightnessUp, 1)]
    [InlineData(HotkeySignal.PanelBrightnessDown, -1)]
    public void A_brightness_key_asks_for_one_step_in_one_direction(HotkeySignal signal, int step)
    {
        var a = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, AllOverlaysOn());

        Assert.Equal(HotkeyOutcome.StepPanelBrightness, a.Outcome);
        Assert.Equal(step, a.Step);
        Assert.Null(a.Mode);
    }

    [Theory]
    [InlineData(HotkeySignal.PanelBrightnessUp)]
    [InlineData(HotkeySignal.PanelBrightnessDown)]
    public void A_brightness_key_carries_no_text_because_the_policy_cannot_know_the_level(HotkeySignal signal)
    {
        // Every other card is worded here. This one cannot be: the number on it is whatever the
        // panel reports after the step, and this class has no panel and no clock. The view model
        // fills it in from what actually happened, and draws nothing if nothing did.
        var a = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, AllOverlaysOn());

        Assert.Equal(string.Empty, a.Text);
    }

    [Theory]
    [InlineData(HotkeySignal.PanelBrightnessUp)]
    [InlineData(HotkeySignal.PanelBrightnessDown)]
    public void The_brightness_keys_are_the_one_overlay_that_is_on_out_of_the_box(HotkeySignal signal)
    {
        // A DELIBERATE EXCEPTION TO "EVERY OVERLAY DEFAULTS OFF". Every other signal either has
        // an on-screen consequence the owner can see - the mode buttons move, the backlight
        // changes - or is something Windows narrates. These two have neither: the firmware draws
        // nothing, Windows draws nothing because the change never reaches its hotkey path, and
        // without a card the only feedback is the screen itself getting brighter.
        var a = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, new HotkeySettings());

        Assert.True(a.ShowOverlay);
    }

    [Theory]
    [InlineData(HotkeySignal.PanelBrightnessUp)]
    [InlineData(HotkeySignal.PanelBrightnessDown)]
    public void An_owner_who_does_not_want_the_brightness_card_can_turn_it_off(HotkeySignal signal)
    {
        // Defaulting on is not the same as being unswitchable. The doing and the drawing stay
        // separate here as everywhere else: turning the card off leaves the key working.
        var settings = AllOverlaysOn();
        settings.OverlayForPanelBrightness = false;

        var a = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, settings);

        Assert.False(a.ShowOverlay);
        Assert.Equal(HotkeyOutcome.StepPanelBrightness, a.Outcome);
    }

    [Fact]
    public void The_brightness_switch_governs_the_keys_and_never_the_nine_byte_report()
    {
        // The one switch that could be misread as re-enabling the thing the release exists to
        // suppress. It cannot: the report is classified Ignore before anyone asks about drawing.
        foreach (var settings in EverySettingsCombination())
        {
            if (!settings.Enabled) continue;

            Assert.Equal(HotkeyAction.None, HotkeyPolicy.Decide(
                new HotkeyEvent(HotkeySignal.DisplayBrightness, 70), FanMode.Normal, settings));

            foreach (var key in BrightnessKeys)
                Assert.Equal(
                    settings.OverlayForPanelBrightness,
                    HotkeyPolicy.Decide(new HotkeyEvent(key), FanMode.Normal, settings).ShowOverlay);
        }
    }

    [Fact]
    public void Volume_is_suppressed_by_never_being_a_signal_at_all()
    {
        // The other half of the same rule, and the reason brightness needs a test while volume
        // does not: volume keys live on the consumer-control collection this app deliberately
        // never registers, so there is nothing here to decide about. If a Volume member ever
        // appears in the enum, that structural guarantee has been given up somewhere upstream.
        Assert.DoesNotContain(
            Enum.GetNames<HotkeySignal>(),
            name => name.Contains("Volume", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Nothing_outside_the_serviced_set_can_ever_ask_for_an_overlay()
    {
        foreach (var signal in Enum.GetValues<HotkeySignal>())
        {
            var a = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, AllOverlaysOn());
            if (Serviced.Contains(signal)) continue;
            Assert.False(a.ShowOverlay, $"{signal} asked for an overlay");
            Assert.Equal(HotkeyOutcome.Ignore, a.Outcome);
        }
    }

    [Fact]
    public void An_unserviced_signal_is_the_same_nothing_under_every_settings_file()
    {
        foreach (var settings in EverySettingsCombination())
        foreach (var signal in Enum.GetValues<HotkeySignal>())
        {
            if (Serviced.Contains(signal)) continue;

            // Not just no overlay: no text either. An unserviced signal must not reach the view
            // model carrying something it could decide to show.
            Assert.Equal(HotkeyAction.None, HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Gaming, settings));
        }
    }

    [Fact]
    public void The_overlay_is_off_unless_the_owner_switched_that_signal_on()
    {
        var settings = new HotkeySettings { Enabled = true, OverlayForFanMode = true };

        var fan = HotkeyPolicy.Decide(new HotkeyEvent(HotkeySignal.FanModeStealth), FanMode.Quiet, settings);
        var wifi = HotkeyPolicy.Decide(new HotkeyEvent(HotkeySignal.WifiEnabled), FanMode.Quiet, settings);

        Assert.True(fan.ShowOverlay);
        Assert.False(wifi.ShowOverlay);
        // Still an action - the overlay toggle governs the drawing, never the doing.
        Assert.Equal(HotkeyOutcome.Notify, wifi.Outcome);
    }

    [Fact]
    public void Each_overlay_switch_governs_only_its_own_signal()
    {
        foreach (var settings in EverySettingsCombination())
        {
            if (!settings.Enabled) continue;

            var fan = HotkeyPolicy.Decide(new HotkeyEvent(HotkeySignal.FanModeStealth), FanMode.Quiet, settings);
            var backlight = HotkeyPolicy.Decide(
                new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 50), FanMode.Quiet, settings);
            var touchpad = HotkeyPolicy.Decide(new HotkeyEvent(HotkeySignal.TouchpadDisabled), FanMode.Quiet, settings);
            var wifi = HotkeyPolicy.Decide(new HotkeyEvent(HotkeySignal.WifiEnabled), FanMode.Quiet, settings);
            var panel = HotkeyPolicy.Decide(new HotkeyEvent(HotkeySignal.PanelBrightnessUp), FanMode.Quiet, settings);

            Assert.Equal(settings.OverlayForFanMode, fan.ShowOverlay);
            Assert.Equal(settings.OverlayForBacklight, backlight.ShowOverlay);
            Assert.Equal(settings.OverlayForTouchpad, touchpad.ShowOverlay);
            Assert.Equal(settings.OverlayForWifi, wifi.ShowOverlay);
            Assert.Equal(settings.OverlayForPanelBrightness, panel.ShowOverlay);
        }
    }

    /// <summary>
    /// Out of the box the app draws for the two brightness keys and for nothing else.
    /// </summary>
    /// <remarks>
    /// This used to read "draws nothing at all", which was the whole point of v0.3: the complaint
    /// being fixed was a second card nobody asked for. The exception is narrow and is argued for
    /// where the default lives, in <see cref="HotkeySettings.OverlayForPanelBrightness"/> - these
    /// two keys are the only ones with no feedback of any kind behind them, because the firmware
    /// does not act on them and Windows never sees the change this app makes instead.
    /// </remarks>
    [Fact]
    public void A_defaulted_settings_object_draws_for_the_brightness_keys_and_nothing_else()
    {
        var settings = new HotkeySettings();

        foreach (var signal in Enum.GetValues<HotkeySignal>())
        {
            var drawn = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, settings).ShowOverlay;
            Assert.Equal(BrightnessKeys.Contains(signal), drawn);
        }
    }

    [Fact]
    public void Switching_hotkeys_off_stops_every_signal_dead()
    {
        var settings = AllOverlaysOn();
        settings.Enabled = false;

        foreach (var signal in Enum.GetValues<HotkeySignal>())
            Assert.Equal(HotkeyAction.None, HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, settings));
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => HotkeyPolicy.Decide(null!, FanMode.Normal, new HotkeySettings()));
        Assert.Throws<ArgumentNullException>(
            () => HotkeyPolicy.Decide(new HotkeyEvent(HotkeySignal.WifiEnabled), FanMode.Normal, null!));
    }
}
