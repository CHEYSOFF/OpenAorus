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
    };

    /// <summary>Every combination of the five switches an owner can throw, in a fixed order.</summary>
    /// <remarks>Five booleans is thirty-two settings files, which is small enough to walk
    /// exhaustively - and walking them is the only way to say "no setting can do this" rather
    /// than "the settings I thought of cannot do this". The five names and the bound are written
    /// out by hand, so <see cref="The_walk_still_covers_every_switch_there_is"/> is what keeps
    /// them honest as the settings grow.</remarks>
    private static IEnumerable<HotkeySettings> EverySettingsCombination()
    {
        for (var bits = 0; bits < 32; bits++)
        {
            yield return new HotkeySettings
            {
                Enabled = (bits & 1) != 0,
                OverlayForFanMode = (bits & 2) != 0,
                OverlayForBacklight = (bits & 4) != 0,
                OverlayForTouchpad = (bits & 8) != 0,
                OverlayForWifi = (bits & 16) != 0,
            };
        }
    }

    /// <summary>
    /// The walk above still visits the whole space, and not half of it.
    /// </summary>
    /// <remarks>
    /// The strongest guarantee in this release - "no settings file can make display brightness
    /// draw" - rests on a hand-written list of five property names and a hand-written bound of
    /// 32. A sixth boolean added to <see cref="HotkeySettings"/> would leave that walk compiling
    /// and passing while covering half the settings files there are, with its own comment still
    /// claiming otherwise. Nothing else in the project would say a word.
    ///
    /// So the shape is asserted rather than assumed. Adding a switch fails here, loudly, and the
    /// failure names the walk that has to be extended: one more name in the list, one more bit,
    /// and 32 becomes 64.
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
                "OverlayForTouchpad",
                "OverlayForWifi",
            },
            switches);

        // And the walk is sized to them rather than to the number that was true when it was
        // written: 2^5 files, each one distinct.
        var combinations = EverySettingsCombination().ToArray();
        Assert.Equal(1 << switches.Length, combinations.Length);
        Assert.Equal(
            combinations.Length,
            combinations
                .Select(s => (s.Enabled, s.OverlayForFanMode, s.OverlayForBacklight, s.OverlayForTouchpad, s.OverlayForWifi))
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
    public void Display_brightness_never_draws_an_overlay_whatever_is_switched_on()
    {
        // The one signal where "we never draw over Windows' own overlay" is a decision rather
        // than a structural fact: volume never reaches this app, but brightness does, on the
        // 0xFF00/0xFF00 collection. This is the test standing in for the missing registration.
        var a = HotkeyPolicy.Decide(
            new HotkeyEvent(HotkeySignal.DisplayBrightness, 80), FanMode.Turbo, AllOverlaysOn());

        Assert.False(a.ShowOverlay);
        Assert.Equal(HotkeyAction.None, a);
    }

    [Fact]
    public void No_settings_file_can_make_display_brightness_draw()
    {
        // Not "with the overlays I switched on" but "with every combination of switches there
        // is". The suppression has to be unreachable from the settings, not merely absent from
        // the defaults, because a second brightness overlay on top of Windows' own is the
        // complaint this release exists to answer.
        foreach (var settings in EverySettingsCombination())
        foreach (var level in new[] { 0, 42, 80, 255 })
        {
            var a = HotkeyPolicy.Decide(
                new HotkeyEvent(HotkeySignal.DisplayBrightness, level), FanMode.Normal, settings);

            Assert.Equal(HotkeyAction.None, a);
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
        var serviced = new[]
        {
            HotkeySignal.FanModeStealth, HotkeySignal.FanModeAutoLow, HotkeySignal.FanModeAutoHigh,
            HotkeySignal.KeyboardBacklightLevel,
            HotkeySignal.TouchpadEnabled, HotkeySignal.TouchpadDisabled,
            HotkeySignal.WifiEnabled, HotkeySignal.WifiDisabled,
        };

        foreach (var signal in Enum.GetValues<HotkeySignal>())
        {
            var a = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, AllOverlaysOn());
            if (serviced.Contains(signal)) continue;
            Assert.False(a.ShowOverlay, $"{signal} asked for an overlay");
            Assert.Equal(HotkeyOutcome.Ignore, a.Outcome);
        }
    }

    [Fact]
    public void An_unserviced_signal_is_the_same_nothing_under_every_settings_file()
    {
        var serviced = new[]
        {
            HotkeySignal.FanModeStealth, HotkeySignal.FanModeAutoLow, HotkeySignal.FanModeAutoHigh,
            HotkeySignal.KeyboardBacklightLevel,
            HotkeySignal.TouchpadEnabled, HotkeySignal.TouchpadDisabled,
            HotkeySignal.WifiEnabled, HotkeySignal.WifiDisabled,
        };

        foreach (var settings in EverySettingsCombination())
        foreach (var signal in Enum.GetValues<HotkeySignal>())
        {
            if (serviced.Contains(signal)) continue;

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

            Assert.Equal(settings.OverlayForFanMode, fan.ShowOverlay);
            Assert.Equal(settings.OverlayForBacklight, backlight.ShowOverlay);
            Assert.Equal(settings.OverlayForTouchpad, touchpad.ShowOverlay);
            Assert.Equal(settings.OverlayForWifi, wifi.ShowOverlay);
        }
    }

    [Fact]
    public void A_defaulted_settings_object_draws_nothing_at_all()
    {
        var settings = new HotkeySettings();

        foreach (var signal in Enum.GetValues<HotkeySignal>())
            Assert.False(HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, settings).ShowOverlay);
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
