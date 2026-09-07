using System.IO;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The hotkey half of the settings file: what it defaults to, what happens when it is wrong, and
/// which of the two notices the owner gets.
/// </summary>
/// <remarks>
/// <para>
/// This follows <see cref="FanSettingsRepairTests"/> rather than inventing anything: a value the
/// app would not accept is repaired inside <see cref="SettingsStore.Load"/> and reported through
/// <c>LastLoadRepaired</c>, while a file that cannot be read at all is a full reset with a
/// <c>.bad</c> rename reported through <c>LastLoadWasReset</c>.
/// </para>
/// <para>
/// Telling those two apart is most of the point of these tests. A reset also produces defaults,
/// so a test that only checked "OverlaySeconds came back as 1" would pass just as happily if the
/// repair were deleted and the whole file thrown away instead. Every repair test here therefore
/// asserts that something unrelated in the file survived and that no <c>.bad</c> file was
/// written - see <see cref="AssertRepairedNotReset"/>.
/// </para>
/// </remarks>
public class HotkeySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_dir, "settings.json");
    private string BadFile => File + ".bad";

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private SettingsStore StoreOver(string json)
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, json);
        return new SettingsStore(File);
    }

    /// <summary>Asserts the store took the milder of the two routes: the file was read, a value
    /// inside it was replaced, and nothing else was thrown away.</summary>
    private void AssertRepairedNotReset(SettingsStore store)
    {
        Assert.True(store.LastLoadHotkeysRepaired);
        Assert.True(store.LastLoadRepaired);
        // The three things that separate a repair from a reset. Without them this whole file
        // would pass against a Load() that deleted the settings and started over.
        Assert.False(store.LastLoadWasReset);
        Assert.False(System.IO.File.Exists(BadFile));
        Assert.True(System.IO.File.Exists(File));
    }

    /// <summary>Asserts the store took the drastic route: the file could not be read, so it was
    /// set aside and defaults returned.</summary>
    private void AssertReset(SettingsStore store)
    {
        Assert.True(store.LastLoadWasReset);
        Assert.True(System.IO.File.Exists(BadFile));
        // A reset is not a repair. Claiming both would tell the owner their file was kept.
        Assert.False(store.LastLoadHotkeysRepaired);
        Assert.False(store.LastLoadRepaired);
    }

    // ---- the type itself -------------------------------------------------------------------

    [Fact]
    public void The_channels_are_on_and_every_overlay_but_one_is_off_out_of_the_box()
    {
        var s = new HotkeySettings();

        // Listening is the feature. Drawing is what the owner complained about.
        Assert.True(s.Enabled);
        Assert.False(s.OverlayForFanMode);
        Assert.False(s.OverlayForBacklight);
        Assert.False(s.OverlayForTouchpad);
        Assert.False(s.OverlayForWifi);

        // The exception, and the only one. The two panel-brightness keys have no other feedback
        // of any kind: the firmware reports them and does not act on them, and the change this
        // app makes instead never travels through Windows' hotkey path, so Windows draws nothing
        // either. Without this card the key looks broken. The argument is set out in full on the
        // property itself.
        Assert.True(s.OverlayForPanelBrightness);
    }

    [Fact]
    public void A_fresh_instance_needs_no_repair()
    {
        Assert.False(new HotkeySettings().Repair());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(999)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void An_overlay_duration_from_outside_the_range_is_brought_back_in(int seconds)
    {
        var s = new HotkeySettings { OverlaySeconds = seconds };

        Assert.True(s.Repair());
        Assert.InRange(s.OverlaySeconds, HotkeySettings.MinOverlaySeconds, HotkeySettings.MaxOverlaySeconds);
    }

    [Theory]
    [InlineData(HotkeySettings.MinOverlaySeconds)]
    [InlineData(3)]
    [InlineData(HotkeySettings.MaxOverlaySeconds)]
    public void A_duration_already_in_range_is_left_alone(int seconds)
    {
        var s = new HotkeySettings { OverlaySeconds = seconds };

        Assert.False(s.Repair());
        Assert.Equal(seconds, s.OverlaySeconds);
    }

    [Fact]
    public void Repair_touches_nothing_but_the_duration()
    {
        // The four toggles are booleans: there is no such thing as an out-of-range one, so a
        // repair that "fixed" them would only ever be silently undoing the owner's choices.
        var s = new HotkeySettings
        {
            Enabled = false,
            OverlayForFanMode = true,
            OverlayForBacklight = true,
            OverlayForTouchpad = true,
            OverlayForWifi = true,
            OverlaySeconds = 0,
        };

        Assert.True(s.Repair());
        Assert.False(s.Enabled);
        Assert.True(s.OverlayForFanMode);
        Assert.True(s.OverlayForBacklight);
        Assert.True(s.OverlayForTouchpad);
        Assert.True(s.OverlayForWifi);
    }

    [Fact]
    public void Repair_is_idempotent()
    {
        var s = new HotkeySettings { OverlaySeconds = 40 };
        Assert.True(s.Repair());
        Assert.False(s.Repair());
    }

    // ---- hanging off AppSettings -----------------------------------------------------------

    [Fact]
    public void Settings_written_without_a_hotkey_section_still_load()
    {
        // Every v0.1 and v0.2 settings.json is this file.
        var settings = new AppSettings { Hotkeys = null! };

        Assert.NotNull(settings.Hotkeys);
        Assert.True(settings.Hotkeys.Enabled);
    }

    // ---- through the store ------------------------------------------------------------------

    [Fact]
    public void A_broken_hotkey_section_is_repaired_on_load_and_reported()
    {
        var store = StoreOver(
            """{"Mode":"Gaming","ChargeStopPercent":65,"Hotkeys":{"Enabled":true,"OverlayForWifi":true,"OverlaySeconds":0}}""");

        var loaded = store.Load();

        AssertRepairedNotReset(store);
        Assert.Equal(HotkeySettings.MinOverlaySeconds, loaded.Hotkeys.OverlaySeconds);
        // The rest of the file survives: this is the milder notice, not a reset. A reset would
        // have handed back Normal and 80 and every overlay off.
        Assert.True(loaded.Hotkeys.Enabled);
        Assert.True(loaded.Hotkeys.OverlayForWifi);
        Assert.Equal(FanMode.Gaming, loaded.Mode);
        Assert.Equal(65, loaded.ChargeStopPercent);
    }

    [Fact]
    public void A_sound_hotkey_section_reports_no_repair()
    {
        var store = StoreOver("""{"Hotkeys":{"Enabled":false,"OverlayForFanMode":true,"OverlaySeconds":2}}""");

        var loaded = store.Load();

        Assert.False(store.LastLoadHotkeysRepaired);
        Assert.False(store.LastLoadRepaired);
        Assert.False(store.LastLoadWasReset);
        Assert.False(loaded.Hotkeys.Enabled);
        Assert.True(loaded.Hotkeys.OverlayForFanMode);
        Assert.Equal(2, loaded.Hotkeys.OverlaySeconds);
    }

    [Fact]
    public void A_file_from_before_the_hotkeys_existed_loads_with_defaults_and_no_notice()
    {
        var store = StoreOver("""{"Mode":"Quiet","FixedPercent":50}""");

        var loaded = store.Load();

        Assert.False(store.LastLoadWasReset);
        Assert.False(store.LastLoadHotkeysRepaired);
        Assert.NotNull(loaded.Hotkeys);
        Assert.True(loaded.Hotkeys.Enabled);
        Assert.False(loaded.Hotkeys.OverlayForFanMode);
        Assert.Equal(HotkeySettings.MinOverlaySeconds, loaded.Hotkeys.OverlaySeconds);
        Assert.Equal(FanMode.Quiet, loaded.Mode);
    }

    [Fact]
    public void An_explicitly_null_hotkey_section_loads_with_defaults_rather_than_resetting()
    {
        // "Hotkeys": null is valid JSON of the wrong shape. The property setter absorbs it, so
        // there is nothing to repair and nothing to reset - the rest of the file is untouched.
        var store = StoreOver("""{"Mode":"Turbo","Hotkeys":null}""");

        var loaded = store.Load();

        Assert.False(store.LastLoadWasReset);
        Assert.False(System.IO.File.Exists(BadFile));
        Assert.False(store.LastLoadHotkeysRepaired);
        Assert.NotNull(loaded.Hotkeys);
        Assert.True(loaded.Hotkeys.Enabled);
        Assert.Equal(FanMode.Turbo, loaded.Mode);
    }

    [Fact]
    public void A_hotkey_repair_does_not_blame_the_lighting_or_the_fans()
    {
        var store = StoreOver("""{"Hotkeys":{"OverlaySeconds":-1}}""");

        store.Load();

        AssertRepairedNotReset(store);
        Assert.False(store.LastLoadLightingRepaired);
        Assert.False(store.LastLoadFansRepaired);
    }

    [Fact]
    public void A_lighting_or_fan_repair_does_not_blame_the_hotkeys()
    {
        var store = StoreOver("""{"FixedPercent":0,"Lighting":{"Effect":99}}""");

        store.Load();

        Assert.True(store.LastLoadFansRepaired);
        Assert.True(store.LastLoadLightingRepaired);
        Assert.False(store.LastLoadHotkeysRepaired);
    }

    [Fact]
    public void All_three_halves_can_be_repaired_in_one_load()
    {
        var store = StoreOver(
            """{"FixedPercent":0,"Lighting":{"BrightnessPercent":500},"Hotkeys":{"OverlaySeconds":90}}""");

        var loaded = store.Load();

        AssertRepairedNotReset(store);
        Assert.True(store.LastLoadFansRepaired);
        Assert.True(store.LastLoadLightingRepaired);
        Assert.Equal(HotkeySettings.MaxOverlaySeconds, loaded.Hotkeys.OverlaySeconds);
        Assert.Equal(FanSafety.MinFixedPercent, loaded.FixedPercent);
        Assert.Equal(100, loaded.Lighting.BrightnessPercent);
    }

    [Fact]
    public void The_toggles_survive_a_save_and_a_load()
    {
        Directory.CreateDirectory(_dir);
        var store = new SettingsStore(File);
        var settings = new AppSettings();
        settings.Hotkeys.OverlayForFanMode = true;
        settings.Hotkeys.OverlayForWifi = true;
        settings.Hotkeys.OverlaySeconds = 4;
        store.Save(settings);

        var reader = new SettingsStore(File);
        var loaded = reader.Load();

        Assert.True(loaded.Hotkeys.OverlayForFanMode);
        Assert.True(loaded.Hotkeys.OverlayForWifi);
        Assert.False(loaded.Hotkeys.OverlayForBacklight);
        Assert.Equal(4, loaded.Hotkeys.OverlaySeconds);
        // What this app writes is never something it then has to repair.
        Assert.False(reader.LastLoadRepaired);
        Assert.False(reader.LastLoadWasReset);
    }

    // ---- files that are not merely wrong but unreadable ------------------------------------

    [Fact]
    public void No_settings_file_at_all_gives_defaults_with_no_notice_of_either_kind()
    {
        var store = new SettingsStore(File);

        var loaded = store.Load();

        Assert.False(store.LastLoadWasReset);
        Assert.False(store.LastLoadHotkeysRepaired);
        Assert.True(loaded.Hotkeys.Enabled);
        Assert.Equal(HotkeySettings.MinOverlaySeconds, loaded.Hotkeys.OverlaySeconds);
    }

    [Theory]
    // Empty, whitespace, truncated mid-object, truncated mid-hotkey-section, a bare scalar where
    // the settings object should be, the hotkey section as a number, and a string where the
    // duration is an int. None of these is a value to clamp; each is a file that cannot be read.
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("""{"Mode":"Gaming","Hotkeys":""")]
    [InlineData("""{"Hotkeys":{"Enabled":true,"OverlaySec""")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("""{"Hotkeys":5}""")]
    [InlineData("""{"Hotkeys":[1,2,3]}""")]
    [InlineData("""{"Hotkeys":{"OverlaySeconds":"soon"}}""")]
    [InlineData("""{"Hotkeys":{"Enabled":"maybe"}}""")]
    [InlineData("""{"Mode":"Ludicrous"}""")]
    public void An_unreadable_file_resets_rather_than_throwing_and_still_yields_usable_hotkeys(string json)
    {
        var store = StoreOver(json);

        AppSettings? loaded = null;
        Assert.Null(Record.Exception(() => loaded = store.Load()));

        AssertReset(store);
        Assert.NotNull(loaded);
        Assert.True(loaded!.Hotkeys.Enabled);
        Assert.False(loaded.Hotkeys.OverlayForFanMode);
        Assert.Equal(HotkeySettings.MinOverlaySeconds, loaded.Hotkeys.OverlaySeconds);
    }

    [Fact]
    public void An_out_of_range_duration_is_a_repair_and_a_broken_file_is_a_reset()
    {
        // The pair, side by side, because they are the two things this class must never confuse.
        var repairing = StoreOver("""{"Mode":"Gaming","Hotkeys":{"OverlaySeconds":0}}""");
        var repaired = repairing.Load();
        Assert.False(repairing.LastLoadWasReset);
        Assert.True(repairing.LastLoadHotkeysRepaired);
        Assert.Equal(FanMode.Gaming, repaired.Mode);
        Assert.False(System.IO.File.Exists(BadFile));

        var resetting = StoreOver("""{"Mode":"Gaming","Hotkeys":{"OverlaySeconds":0""");
        var reset = resetting.Load();
        Assert.True(resetting.LastLoadWasReset);
        Assert.False(resetting.LastLoadHotkeysRepaired);
        Assert.Equal(FanMode.Normal, reset.Mode);
        Assert.True(System.IO.File.Exists(BadFile));
    }

    // ---- the rule no settings file may reach ------------------------------------------------

    [Fact]
    public void No_saved_setting_can_make_the_nine_byte_brightness_report_draw()
    {
        // An owner - or a future version, or a hand edit - inventing the switch they wish existed.
        // Unknown properties are ignored on load, and HotkeyPolicy.MayDraw has no arm for the
        // report whatever the file says, so the second overlay stays impossible.
        //
        // NOT the two brightness KEYS, which are drawn for on purpose and have a switch of their
        // own. The report says the brightness has already changed, which means something else
        // changed it and drew its own card; the keys say only that a key was pressed and nothing
        // has happened yet. The file below deliberately spells the invented name the old way.
        var store = StoreOver(
            """
            {"Hotkeys":{"Enabled":true,"OverlayForFanMode":true,"OverlayForBacklight":true,
             "OverlayForTouchpad":true,"OverlayForWifi":true,"OverlaySeconds":5,
             "OverlayForDisplayBrightness":true,"OverlayForVolume":true,"ShowOverlay":true}}
            """);

        var loaded = store.Load();

        Assert.False(store.LastLoadWasReset);
        for (var level = 0; level <= 100; level += 10)
        {
            var a = HotkeyPolicy.Decide(
                new HotkeyEvent(HotkeySignal.DisplayBrightness, level), FanMode.Normal, loaded.Hotkeys);

            Assert.Equal(HotkeyAction.None, a);
            Assert.False(a.ShowOverlay);
        }
    }

    [Fact]
    public void What_load_hands_the_policy_is_always_something_the_policy_will_take()
    {
        var store = StoreOver("""{"Hotkeys":{"OverlaySeconds":-9,"OverlayForFanMode":true}}""");

        var loaded = store.Load();

        AssertRepairedNotReset(store);
        var a = HotkeyPolicy.Decide(new HotkeyEvent(HotkeySignal.FanModeAutoLow), FanMode.Normal, loaded.Hotkeys);
        Assert.Equal(HotkeyOutcome.CycleFanMode, a.Outcome);
        Assert.True(a.ShowOverlay);
        Assert.InRange(loaded.Hotkeys.OverlaySeconds, HotkeySettings.MinOverlaySeconds, HotkeySettings.MaxOverlaySeconds);
    }
}
