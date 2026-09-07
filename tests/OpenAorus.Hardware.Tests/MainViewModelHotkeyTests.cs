using System.IO;
using OpenAorus.App;
using OpenAorus.App.Hotkeys;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Display;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Hotkeys;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;
using OpenAorus.Hardware.Ui;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The other half of the hotkey path: what the view model actually does with an action, driven an
/// action at a time the way <see cref="MainViewModelWatchdogTests"/> drives a poll at a time.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HotkeyServiceTests"/> stops at the decision. These run the decision through a real
/// <see cref="FanController"/>, a real <see cref="LightingController"/> and a fake keyboard, so a
/// wrong write shows up as a wrong report rather than as a passing restatement of the view model.
/// </para>
/// <para>
/// The last test in the file is the one that matters most on a bench: a fan-mode apply is five to
/// seven WMI writes paced at <see cref="FanController.StepDelayMs"/> ms, the debouncer throttles
/// rather than latches, and a held key therefore keeps producing fresh signals for as long as it
/// is held.
/// </para>
/// </remarks>
public class MainViewModelHotkeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class Rig
    {
        public required FakeGigabyteWmi Wmi { get; init; }
        public required FakeKeyboardHid Hid { get; init; }
        public required FakePanelBrightness Panel { get; init; }
        public required AppServices Services { get; init; }
        public required MainViewModel Vm { get; init; }

        /// <summary>Every mode the controller reported as applied, in order.</summary>
        public List<FanMode> Applied { get; } = new();

        /// <summary>Everything the view model asked the window to draw.</summary>
        public List<string> Overlays { get; } = new();
    }

    private Rig Build(
        Func<int, Task>? fanDelay = null,
        bool keyboardPresent = true,
        ModelProfile? model = null,
        PanelBrightnessState? panel = null)
    {
        var profile = model ?? ModelProfile.Detect("AORUS 17G KD");
        var wmi = new FakeGigabyteWmi();
        var hid = new FakeKeyboardHid { IsPresent = keyboardPresent };
        var screen = new FakePanelBrightness
        {
            State = panel ?? new PanelBrightnessState(50, FakePanelBrightness.DenseLadder),
        };
        var services = new AppServices
        {
            PanelBrightness = new PanelBrightnessController(screen),
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile, delay: fanDelay ?? (_ => Task.CompletedTask)),
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = new SettingsStore(Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json")),
            Settings = new AppSettings(),
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = keyboardPresent,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        };

        var rig = new Rig
        {
            Wmi = wmi, Hid = hid, Panel = screen, Services = services, Vm = new MainViewModel(services),
        };
        services.Fans.Applied += rig.Applied.Add;
        rig.Vm.OverlayRequested += rig.Overlays.Add;
        return rig;
    }

    private static HotkeyAction Cycle(FanMode mode, bool overlay = false) =>
        new(HotkeyOutcome.CycleFanMode, mode, 0, $"Fan mode: {mode}", overlay);

    // ---- Acting on one action --------------------------------------------------------

    [Fact]
    public async Task A_fan_cycle_puts_the_machine_on_the_mode_the_policy_chose()
    {
        var rig = Build();

        await rig.Vm.OnHotkeyAsync(Cycle(FanMode.Gaming));

        Assert.Equal(new[] { FanMode.Gaming }, rig.Applied);
        Assert.Equal(FanMode.Gaming, rig.Vm.SelectedMode);
        // The same save a mode click makes, so a hotkey change survives a restart.
        Assert.Equal(FanMode.Gaming, new SettingsStore(rig.Services.Store.Path).Load().Mode);
    }

    [Fact]
    public async Task A_backlight_action_moves_the_slider_and_writes_nothing_to_the_keyboard()
    {
        var rig = Build();
        rig.Hid.ClearWritten();

        await rig.Vm.OnHotkeyAsync(new HotkeyAction(HotkeyOutcome.SetBacklightLevel, null, 100, "Keyboard backlight 100 %"));
        await rig.Vm.Lighting.LiveWrites;

        Assert.Equal(100, rig.Vm.Lighting.BrightnessPercent);
        // The firmware already moved the backlight. A write here would be a redundant 264-byte
        // report racing it for the same value.
        Assert.Empty(rig.Hid.Written);
        Assert.Equal(100, new SettingsStore(rig.Services.Store.Path).Load().Lighting.BrightnessPercent);
    }

    [Theory]
    [InlineData(-40, 0)]
    [InlineData(1000, 100)]
    public async Task A_backlight_level_outside_the_slider_is_brought_inside_it(int reported, int expected)
    {
        var rig = Build();

        await rig.Vm.OnHotkeyAsync(new HotkeyAction(HotkeyOutcome.SetBacklightLevel, null, reported, "x"));

        Assert.Equal(expected, rig.Vm.Lighting.BrightnessPercent);
    }

    [Fact]
    public async Task A_notice_writes_nothing_anywhere()
    {
        var rig = Build();
        rig.Hid.ClearWritten();

        await rig.Vm.OnHotkeyAsync(new HotkeyAction(HotkeyOutcome.Notify, null, 0, "Touchpad off", true));

        // The firmware has already done it; there is nothing to write and nothing to save.
        Assert.Empty(rig.Wmi.Calls);
        Assert.Empty(rig.Hid.Written);
        Assert.Equal(new[] { "Touchpad off" }, rig.Overlays);
    }

    [Fact]
    public async Task An_action_the_owner_did_not_ask_to_see_draws_nothing()
    {
        var rig = Build();

        await rig.Vm.OnHotkeyAsync(Cycle(FanMode.Normal, overlay: false));

        Assert.Empty(rig.Overlays);
        Assert.Equal(new[] { FanMode.Normal }, rig.Applied);
    }

    [Fact]
    public async Task An_action_the_owner_asked_to_see_is_drawn_once()
    {
        var rig = Build();

        await rig.Vm.OnHotkeyAsync(Cycle(FanMode.Normal, overlay: true));

        Assert.Equal(new[] { "Fan mode: Normal" }, rig.Overlays);
    }

    [Fact]
    public async Task A_null_action_is_this_app_calling_itself_wrongly()
    {
        var rig = Build();

        await Assert.ThrowsAsync<ArgumentNullException>(() => rig.Vm.OnHotkeyAsync(null!));
    }

    [Fact]
    public async Task On_a_model_it_cannot_write_to_the_fan_key_does_nothing()
    {
        var rig = Build(model: ModelProfile.Detect("Some Other Laptop"));

        await rig.Vm.OnHotkeyAsync(Cycle(FanMode.Turbo));

        // The duty scale is a guess on an unrecognised model, and driving the fans off a wrong
        // one is the risk the read-only rule exists for. The key is inert, exactly as the
        // on-screen mode buttons are.
        Assert.Empty(rig.Wmi.Calls);
        Assert.Empty(rig.Applied);
    }

    // ---- The busy guard --------------------------------------------------------------

    [Fact]
    public async Task A_second_press_landing_mid_apply_is_dropped_rather_than_queued()
    {
        // Held open on the first paced step, which is where a real apply spends five to seven
        // half-seconds.
        var gate = new TaskCompletionSource();
        var rig = Build(fanDelay: _ => gate.Task);

        var first = rig.Vm.OnHotkeyAsync(Cycle(FanMode.Normal));
        Assert.False(first.IsCompleted);

        var second = rig.Vm.OnHotkeyAsync(Cycle(FanMode.Gaming));

        // Completed already, and completed having done nothing: the guard drops it here rather
        // than letting it wait on FanController's own gate, where it would land as a second
        // multi-second sequence the moment the first finished.
        Assert.True(second.IsCompleted);

        gate.SetResult();
        await first;
        await second;

        Assert.Equal(new[] { FanMode.Normal }, rig.Applied);
        Assert.Equal(FanMode.Normal, rig.Vm.SelectedMode);
    }

    [Fact]
    public async Task A_press_after_the_apply_has_finished_is_serviced_normally()
    {
        // The guard is a guard, not a latch: it must let go again.
        var rig = Build();

        await rig.Vm.OnHotkeyAsync(Cycle(FanMode.Normal));
        await rig.Vm.OnHotkeyAsync(Cycle(FanMode.Gaming));

        Assert.Equal(new[] { FanMode.Normal, FanMode.Gaming }, rig.Applied);
    }

    // ---- The whole path, source to fan write -----------------------------------------

    [Fact]
    public async Task A_held_fan_key_produces_one_fan_change_rather_than_a_queue()
    {
        var gate = new TaskCompletionSource();
        var rig = Build(fanDelay: _ => gate.Task);

        var raw = new FakeHotkeySource();
        var now = 0L;
        using var hotkeys = new HotkeyService(
            raw, new FakeWmiEventSource(), rig.Services.Settings.Hotkeys, () => rig.Services.Settings.Mode,
            post: w => w(), clock: () => now);

        // Collected rather than dropped, only so the test can wait for them; the app drops them
        // for the same reason the sensor poller does - the raw-input hook has nowhere to await.
        var running = new List<Task>();
        var requested = 0;
        hotkeys.ActionRequested += a => { requested++; running.Add(rig.Vm.OnHotkeyAsync(a)); };
        hotkeys.Start();

        // A held key repeats, and there is no key-up on these collections: the debouncer bounds
        // the rate at one per window, it does not hold anything off until release.
        for (var i = 0; i < 5; i++)
        {
            raw.Emit(4, 0, 0, 39);
            now += SignalDebouncer.WindowMs;
        }

        gate.SetResult();
        await Task.WhenAll(running);

        // Every press really did get through the debouncer - so this is the busy guard's doing
        // and not an accident of the window ...
        Assert.Equal(5, requested);
        // ... and the machine ran one five-to-seven step sequence, not five of them back to back.
        // Gaming, because the settings the service reads start on Normal.
        Assert.Equal(new[] { FanMode.Gaming }, rig.Applied);
        Assert.Equal(FanMode.Gaming, rig.Vm.SelectedMode);
    }

    // ---- The card follows the press, and only a press that was taken ------------------

    [Fact]
    public async Task A_press_that_applies_is_the_one_the_card_reports()
    {
        var rig = Build();

        await rig.Vm.OnHotkeyAsync(Cycle(FanMode.Gaming, overlay: true));

        Assert.Equal(new[] { FanMode.Gaming }, rig.Applied);
        Assert.Equal(new[] { "Fan mode: Gaming" }, rig.Overlays);
    }

    [Fact]
    public async Task The_card_is_up_before_the_writes_are_finished()
    {
        // A real apply is five to seven WMI writes paced at FanController.StepDelayMs, so drawing
        // after it would put the card two to three seconds behind the key. This is being judged
        // against the volume card Windows draws instantly; a late OSD is not an OSD.
        var gate = new TaskCompletionSource();
        var rig = Build(fanDelay: _ => gate.Task);

        var press = rig.Vm.OnHotkeyAsync(Cycle(FanMode.Gaming, overlay: true));

        Assert.False(press.IsCompleted);                            // still mid-sequence ...
        Assert.Equal(new[] { "Fan mode: Gaming" }, rig.Overlays);    // ... and the card is already up

        gate.SetResult();
        await press;
    }

    [Fact]
    public async Task A_press_dropped_mid_apply_draws_nothing()
    {
        var gate = new TaskCompletionSource();
        var rig = Build(fanDelay: _ => gate.Task);

        var first = rig.Vm.OnHotkeyAsync(Cycle(FanMode.Normal, overlay: true));
        var second = rig.Vm.OnHotkeyAsync(Cycle(FanMode.Gaming, overlay: true));

        gate.SetResult();
        await first;
        await second;

        // The second press changed nothing - the busy guard dropped it - so a card reading
        // "Fan mode: Gaming" would narrate a change the machine never made. Worse on a held key:
        // the saved mode only moves on success, so every dropped press names the same target and
        // the owner watches the card promise Gaming over and over while the fans stay on Normal.
        Assert.Equal(new[] { FanMode.Normal }, rig.Applied);
        Assert.Equal(new[] { "Fan mode: Normal" }, rig.Overlays);
    }

    [Fact]
    public async Task A_press_on_a_read_only_model_draws_nothing()
    {
        var rig = Build(model: ModelProfile.Detect("Some Other Laptop"));

        await rig.Vm.OnHotkeyAsync(Cycle(FanMode.Turbo, overlay: true));

        // Nothing was written and nothing ever will be on this model. The banner already says it
        // is unrecognised and read-only; a card claiming Turbo would be the app contradicting its
        // own banner once per keypress.
        Assert.Empty(rig.Wmi.Calls);
        Assert.Empty(rig.Overlays);
    }

    [Fact]
    public async Task A_write_that_fails_after_an_accepted_press_is_the_banner_to_report()
    {
        var rig = Build();
        rig.Wmi.FailOn.Add("SetCurrentFanStep");   // the first step of every sequence

        await rig.Vm.OnHotkeyAsync(Cycle(FanMode.Gaming, overlay: true));

        // The press was taken, so the card went up with it - the failure arrives seconds later and
        // is not something an OSD can wait for. What must not happen is the guard swallowing it:
        // the banner and the status line say so, exactly as they do for a mode click that fails.
        Assert.Equal(new[] { "Fan mode: Gaming" }, rig.Overlays);
        Assert.NotEqual(FanMode.Gaming, rig.Vm.SelectedMode);
        Assert.Equal(BannerKind.Error, rig.Vm.Banner);
        Assert.Contains("SetCurrentFanStep", rig.Vm.BannerText);
        Assert.Equal("Gaming failed", rig.Vm.StatusLine);
    }

    // ---- the brightness keys, which nothing else on the machine services -------------------

    private static HotkeyAction Brightness(int step, bool overlay = true) =>
        new(HotkeyOutcome.StepPanelBrightness, null, 0, "", overlay, step);

    [Fact]
    public async Task A_brightness_key_moves_the_panel_and_the_card_says_where_it_landed()
    {
        var rig = Build();

        await rig.Vm.OnHotkeyAsync(Brightness(BrightnessLadder.Up));

        Assert.Equal(new[] { 60 }, rig.Panel.Written);
        // The policy could not word this card - it does not know what levels the panel accepts -
        // so the number on it has to come from what the panel actually did.
        Assert.Equal(new[] { "Display brightness 60 %" }, rig.Overlays);
    }

    [Fact]
    public async Task The_down_key_moves_the_other_way()
    {
        var rig = Build();

        await rig.Vm.OnHotkeyAsync(Brightness(BrightnessLadder.Down));

        Assert.Equal(new[] { 40 }, rig.Panel.Written);
        Assert.Equal(new[] { "Display brightness 40 %" }, rig.Overlays);
    }

    [Fact]
    public async Task The_card_reports_the_panel_and_never_the_step_that_was_asked_for()
    {
        // A sparse panel: asking to go up from 20 lands on 40, not on 30. A card built from the
        // request rather than from the answer would show a number the screen is not showing.
        var rig = Build(panel: new PanelBrightnessState(20, new[] { 0, 20, 40, 60, 80, 100 }));

        await rig.Vm.OnHotkeyAsync(Brightness(BrightnessLadder.Up));

        Assert.Equal(new[] { 40 }, rig.Panel.Written);
        Assert.Equal(new[] { "Display brightness 40 %" }, rig.Overlays);
    }

    [Fact]
    public async Task A_brightness_key_writes_nothing_to_the_fans_or_the_keyboard()
    {
        var rig = Build();
        rig.Hid.ClearWritten();

        await rig.Vm.OnHotkeyAsync(Brightness(BrightnessLadder.Up));

        Assert.Empty(rig.Wmi.Calls);
        Assert.Empty(rig.Hid.Written);
    }

    [Fact]
    public async Task On_a_machine_with_no_controllable_panel_the_key_is_silent()
    {
        // A desktop, or a lid closed onto an external monitor. Nothing to write, nothing to say -
        // and above all nothing thrown, because this arrives on a raw-input callback.
        var rig = Build(panel: null);
        rig.Panel.State = null;

        await rig.Vm.OnHotkeyAsync(Brightness(BrightnessLadder.Up));

        Assert.Empty(rig.Panel.Written);
        Assert.Empty(rig.Overlays);
    }

    [Fact]
    public async Task A_panel_that_refuses_the_write_draws_nothing()
    {
        var rig = Build();
        rig.Panel.FailNextSet = true;

        await rig.Vm.OnHotkeyAsync(Brightness(BrightnessLadder.Up));

        // The same rule the fan key follows: a card narrating a change the machine did not make
        // is worse than no card, because the owner then distrusts the ones that are telling the
        // truth.
        Assert.Empty(rig.Overlays);
    }

    [Fact]
    public async Task At_the_end_of_its_travel_the_card_still_says_where_the_panel_is()
    {
        // Unlike a refused write, this is the key working: the panel is at full and stays there.
        // Windows' own card behaves the same way, and it is what tells the owner the key is not
        // broken.
        var rig = Build(panel: new PanelBrightnessState(100, FakePanelBrightness.DenseLadder));

        await rig.Vm.OnHotkeyAsync(Brightness(BrightnessLadder.Up));

        Assert.Empty(rig.Panel.Written);
        Assert.Equal(new[] { "Display brightness 100 %" }, rig.Overlays);
    }

    [Fact]
    public async Task An_owner_who_turned_the_brightness_card_off_still_gets_the_brightness()
    {
        var rig = Build();

        await rig.Vm.OnHotkeyAsync(Brightness(BrightnessLadder.Up, overlay: false));

        Assert.Equal(new[] { 60 }, rig.Panel.Written);
        Assert.Empty(rig.Overlays);
    }

    [Fact]
    public async Task A_tap_of_the_brightness_key_moves_the_panel_once_and_not_twice()
    {
        // THE WHOLE PATH, and the one that would be wrong in the way the owner notices first.
        // Every tap puts two reports on the wire - `04 00 00 7E` and then `04 00 00 00` - and if
        // the release decoded to anything the screen would jump two steps per press.
        var rig = Build();

        var raw = new FakeHotkeySource();
        var now = 0L;
        using var hotkeys = new HotkeyService(
            raw, new FakeWmiEventSource(), rig.Services.Settings.Hotkeys, () => rig.Services.Settings.Mode,
            post: w => w(), clock: () => now);

        var running = new List<Task>();
        hotkeys.ActionRequested += a => running.Add(rig.Vm.OnHotkeyAsync(a));
        hotkeys.Start();

        raw.Emit(4, 0, 0, 0x7E);   // press
        raw.Emit(4, 0, 0, 0x00);   // release
        await Task.WhenAll(running);

        Assert.Equal(new[] { 60 }, rig.Panel.Written);
        Assert.Equal(new[] { "Display brightness 60 %" }, rig.Overlays);
    }

    [Fact]
    public async Task Two_taps_walk_the_panel_two_steps()
    {
        // The debouncer throttles by signal and level, so the second tap has to be outside the
        // window to count - the same rule the fan key lives under.
        var rig = Build();

        var raw = new FakeHotkeySource();
        var now = 0L;
        using var hotkeys = new HotkeyService(
            raw, new FakeWmiEventSource(), rig.Services.Settings.Hotkeys, () => rig.Services.Settings.Mode,
            post: w => w(), clock: () => now);

        var running = new List<Task>();
        hotkeys.ActionRequested += a => running.Add(rig.Vm.OnHotkeyAsync(a));
        hotkeys.Start();

        raw.Emit(4, 0, 0, 0x7E);
        raw.Emit(4, 0, 0, 0x00);
        now += SignalDebouncer.WindowMs;
        raw.Emit(4, 0, 0, 0x7E);
        raw.Emit(4, 0, 0, 0x00);
        await Task.WhenAll(running);

        Assert.Equal(new[] { 60, 70 }, rig.Panel.Written);
    }

    [Fact]
    public async Task The_codes_nobody_has_identified_reach_the_view_model_as_nothing()
    {
        // 134 and 135, both press conventions. They are written down by the trace and acted on by
        // nothing; a press of either must not move the panel it happens to be numerically near.
        var rig = Build();

        var raw = new FakeHotkeySource();
        using var hotkeys = new HotkeyService(
            raw, new FakeWmiEventSource(), rig.Services.Settings.Hotkeys, () => rig.Services.Settings.Mode,
            post: w => w(), clock: () => 0);

        var running = new List<Task>();
        hotkeys.ActionRequested += a => running.Add(rig.Vm.OnHotkeyAsync(a));
        hotkeys.Start();

        raw.Emit(4, 0, 1, 0x86);
        raw.Emit(4, 0, 0, 0x86);
        raw.Emit(4, 0, 1, 0x87);
        raw.Emit(4, 0, 0, 0x87);
        await Task.WhenAll(running);

        Assert.Empty(rig.Panel.Written);
        Assert.Empty(rig.Overlays);
        Assert.Empty(rig.Wmi.Calls);
    }

    [Fact]
    public async Task A_wmi_notice_reaches_the_overlay_through_the_service()
    {
        var rig = Build();
        var events = new FakeWmiEventSource();
        using var hotkeys = new HotkeyService(
            new FakeHotkeySource(), events, rig.Services.Settings.Hotkeys, () => rig.Services.Settings.Mode,
            post: w => w(), clock: () => 0);

        rig.Services.Settings.Hotkeys.OverlayForTouchpad = true;
        var running = new List<Task>();
        hotkeys.ActionRequested += a => running.Add(rig.Vm.OnHotkeyAsync(a));
        hotkeys.Start();

        events.Emit(458);
        await Task.WhenAll(running);

        Assert.Equal(new[] { "Touchpad on" }, rig.Overlays);
        Assert.Empty(rig.Wmi.Calls);
    }
}
