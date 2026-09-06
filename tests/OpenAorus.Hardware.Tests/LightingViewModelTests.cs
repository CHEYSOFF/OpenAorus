using System.IO;
using OpenAorus.App;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;
using OpenAorus.Hardware.Ui;
using MediaColor = System.Windows.Media.Color;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Drives the view model over a real <see cref="LightingController"/> and a fake HID, the same
/// way <see cref="AppServicesTests"/> does, so these catch a protocol regression rather than
/// only restating the view model back to itself.
/// </summary>
public class LightingViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static Task NoDelay(int _) => Task.CompletedTask;

    private sealed class Harness
    {
        public required FakeKeyboardHid Hid { get; init; }
        public required AppServices Services { get; init; }
        public List<(BannerKind Kind, string Text)> Banners { get; } = new();

        public LightingSettings Saved => Services.Settings.Lighting;

        public LightingViewModel ViewModel() => new(Services, (kind, text) => Banners.Add((kind, text)));

        /// <summary>Every 0x02 "set effect" report, in the order it went out.</summary>
        public List<byte[]> EffectReports => Hid.Written.Where(r => r[1] == 0x02).ToList();

        /// <summary>What the settings file actually holds, read back rather than assumed.</summary>
        public AppSettings Reload() => new SettingsStore(Services.Store.Path).Load();
    }

    private Harness Build(Func<int, Task>? delay = null, bool keyboardPresent = true, string? settingsPath = null)
    {
        var hid = new FakeKeyboardHid { IsPresent = keyboardPresent };
        var wmi = new FakeGigabyteWmi();
        var profile = ModelProfile.Detect("AORUS 17G KD");
        // A directory of its own per harness: these tests really save, and really read back.
        var path = settingsPath ?? Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json");

        return new Harness
        {
            Hid = hid,
            Services = new AppServices
            {
                Profile = profile,
                Wmi = wmi,
                Fans = new FanController(wmi, profile, delay: _ => Task.CompletedTask),
                Sensors = new SensorReader(wmi, profile),
                Battery = new BatteryController(wmi),
                Store = new SettingsStore(path),
                Settings = new AppSettings(),
                Gcc = new FakeGccSystem(),
                Lighting = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUk), delay ?? NoDelay),
                KeyboardPresent = keyboardPresent,
                Version = "0.0.0",
                ExePath = "OpenAorus.Tests.exe",
            },
        };
    }

    private static List<RgbColor> Slots(RgbColor c) => Enumerable.Repeat(c, KeyLayout.SlotCount).ToList();

    /// <summary>
    /// A settings path whose parent is a file, so the <c>Directory.CreateDirectory</c> inside
    /// <see cref="SettingsStore.Save"/> throws. A read-only directory would need privileges the
    /// test runner may not have; this needs none.
    /// </summary>
    private string UnwritableSettingsPath()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the settings directory would have to go");
        return Path.Combine(blocker, "settings.json");
    }

    // ---- Starting state -------------------------------------------------------------

    [Fact]
    public void The_starting_state_is_the_saved_lighting()
    {
        var h = Build();
        h.Saved.Effect = LightEffect.Wave;
        h.Saved.Color = new RgbColor(0x11, 0x22, 0x33);
        h.Saved.SecondColor = new RgbColor(0x44, 0x55, 0x66);
        h.Saved.SpeedPercent = 70;
        h.Saved.BrightnessPercent = 33;
        h.Saved.Direction = LightDirection.Left;
        h.Saved.Random = true;

        var vm = h.ViewModel();

        Assert.Equal(LightEffect.Wave, vm.SelectedEffect);
        Assert.Equal(MediaColor.FromRgb(0x11, 0x22, 0x33), vm.PickedColor);
        Assert.Equal(MediaColor.FromRgb(0x44, 0x55, 0x66), vm.PickedSecondColor);
        Assert.Equal(70, vm.SpeedPercent);
        Assert.Equal(33, vm.BrightnessPercent);
        Assert.Equal(LightDirection.Left, vm.Direction);
        Assert.True(vm.Random);

        // Reading the saved state is not applying it: that is AppServices.ApplySavedAsync's job,
        // and a write from the constructor would fight the one it does at startup.
        Assert.Empty(h.Hid.Written);
    }

    [Fact]
    public void The_capability_flags_follow_the_selected_effect()
    {
        var vm = Build().ViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.SelectedEffect = LightEffect.Flow;

        Assert.False(vm.SupportsColor);      // Flow picks its own colours
        Assert.False(vm.SupportsSecondColor);
        Assert.True(vm.SupportsSpeed);
        Assert.True(vm.SupportsDirection);
        Assert.False(vm.SupportsRandom);
        Assert.Contains(nameof(LightingViewModel.SupportsColor), changed);
        Assert.Contains(nameof(LightingViewModel.SupportsDirection), changed);

        vm.SelectedEffect = LightEffect.Merge;

        Assert.True(vm.SupportsSecondColor);
        Assert.False(vm.SupportsDirection);
    }

    // ---- Writing --------------------------------------------------------------------

    [Fact]
    public async Task Applying_an_effect_writes_it_and_saves_it()
    {
        var h = Build();
        var vm = h.ViewModel();
        vm.SelectedEffect = LightEffect.Breathing;
        vm.PickedColor = MediaColor.FromRgb(0x0A, 0x0B, 0x0C);
        vm.BrightnessPercent = 64;

        await vm.ApplyEffectCommand.ExecuteAsync(null);

        var report = h.EffectReports[^1];
        Assert.Equal((byte)LightEffect.Breathing, report[10]);
        Assert.Equal(64, report[12]);
        // Breathing's slice is [speed, firmware mode, R, G, B] at 13 + 4.
        Assert.Equal(new byte[] { 0x0A, 0x0B, 0x0C }, report[(13 + 4 + 2)..(13 + 4 + 5)]);

        var reloaded = h.Reload().Lighting;
        Assert.Equal(LightEffect.Breathing, reloaded.Effect);
        Assert.Equal(new RgbColor(0x0A, 0x0B, 0x0C), reloaded.Color);
        Assert.Equal(64, reloaded.BrightnessPercent);
        Assert.Equal("Breathing applied", vm.StatusText);
    }

    /// <summary>
    /// The reason the write path has a latch at all. A slider bound straight to a write fires
    /// once per pixel of travel; the controller paces reports 65 ms apart behind a semaphore,
    /// so those would queue up and take seconds to drain - and sending them faster is what can
    /// wedge the keyboard's controller until it is replugged. Only the newest value survives.
    /// </summary>
    [Fact]
    public async Task A_slider_value_superseded_while_a_write_is_in_flight_never_reaches_the_keyboard()
    {
        Harness? harness = null;
        var gate = new SemaphoreSlim(0);
        // Each sequence pauses once, between its status read and its write, so the pacing hook is
        // where the burst can be inspected mid-flight: on the second call the first value has
        // just landed on the keyboard with a newer one still waiting behind it.
        var settingsFileAtEachPause = new List<bool>();
        var h = harness = Build(delay: async _ =>
        {
            settingsFileAtEachPause.Add(File.Exists(harness!.Services.Store.Path));
            await gate.WaitAsync();
        });
        var vm = h.ViewModel();

        vm.BrightnessPercent = 10;              // starts a write, which parks on the pacing delay
        Assert.Single(h.Hid.Written);           // only the 0x82 status read has gone out
        vm.BrightnessPercent = 20;              // these three are the rest of the drag
        vm.BrightnessPercent = 30;
        vm.BrightnessPercent = 40;

        gate.Release(8);
        await vm.LiveWrites;

        // Two pauses, one per sequence - and at neither of them had settings.json been written.
        // A drag is one save, at the end, not one per value that reaches the keyboard: saving on
        // every landed write would rewrite the file some twenty times across a three-second drag.
        Assert.Equal(new[] { false, false }, settingsFileAtEachPause);

        // Two sequences, not four: the value in flight, then the newest. 20 and 30 were
        // superseded while they waited and were dropped without ever being sent.
        Assert.Equal(4, h.Hid.Written.Count);
        var brightnesses = h.EffectReports.Select(r => (int)r[12]).ToList();
        Assert.Equal(new[] { 10, 40 }, brightnesses);

        // And only the value that ended the burst reached the disk.
        Assert.Equal(40, h.Reload().Lighting.BrightnessPercent);
    }

    [Fact]
    public async Task A_failed_write_reaches_the_owner()
    {
        var h = Build();
        h.Hid.FailWriteAt = 1; // the 0x02 report; the 0x82 status read ahead of it lands
        var vm = h.ViewModel();

        vm.BrightnessPercent = 25;
        await vm.LiveWrites;

        var (kind, text) = Assert.Single(h.Banners);
        Assert.Equal(BannerKind.Error, kind);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Equal("Failed", vm.StatusText);
        // Nothing landed, so nothing is persisted as if it had.
        Assert.False(File.Exists(h.Services.Store.Path));
    }

    /// <summary>
    /// A save that cannot happen is the owner's problem, not a crash and not a silence. A plain
    /// property change is fire-and-forget - nothing awaits the drain loop - so a throw from
    /// <see cref="SettingsStore.Save"/> inside it would die in a task no one observes: keyboard
    /// changed, nothing persisted, status line still saying it applied. The same throw out of
    /// SavePreset escapes a synchronous command and reaches the dispatcher instead.
    /// </summary>
    [Fact]
    public async Task A_settings_file_that_cannot_be_written_is_reported_and_leaves_the_panel_writing()
    {
        var h = Build(settingsPath: UnwritableSettingsPath());
        var vm = h.ViewModel();

        vm.BrightnessPercent = 25;
        await vm.LiveWrites;   // must not throw, and must not swallow the failure either

        var (kind, text) = Assert.Single(h.Banners);
        Assert.Equal(BannerKind.Error, kind);
        Assert.Contains("could not be saved", text, StringComparison.OrdinalIgnoreCase);

        // And the latch recovered: the next change still reaches the keyboard.
        var before = h.Hid.Written.Count;
        vm.BrightnessPercent = 26;
        await vm.LiveWrites;
        Assert.True(h.Hid.Written.Count > before);
        Assert.Equal(26, h.EffectReports[^1][12]);
    }

    /// <summary>
    /// Cancellation throws out of the controller where every other failure returns a failed
    /// result. That is deliberate - a caller that cancelled has no error to show anyone - so
    /// the view model has to tell the two apart instead of banner-ing the exception.
    /// </summary>
    [Fact]
    public async Task A_cancelled_write_is_not_reported_as_a_failure()
    {
        var gate = new SemaphoreSlim(0);
        var h = Build(delay: async _ => await gate.WaitAsync());
        var vm = h.ViewModel();

        vm.BrightnessPercent = 25;   // parks on the pacing delay, mid-sequence
        vm.Shutdown();               // the window is closing under it
        gate.Release(8);
        await vm.LiveWrites;         // must not throw

        Assert.Empty(h.Banners);
        Assert.NotEqual("Failed", vm.StatusText);
        Assert.DoesNotContain(h.Hid.Written, r => r[1] == 0x02); // stopped between reports
    }

    [Fact]
    public async Task An_absent_keyboard_writes_nothing()
    {
        var h = Build(keyboardPresent: false);
        var vm = h.ViewModel();

        Assert.False(vm.KeyboardPresent);
        Assert.Contains("No supported keyboard", vm.DeviceLine);

        vm.BrightnessPercent = 80;
        await vm.ApplyEffectCommand.ExecuteAsync(null);

        Assert.Empty(h.Hid.Written);
        Assert.Empty(h.Banners);
    }

    [Fact]
    public void A_present_keyboard_names_its_slot_order()
    {
        var vm = Build().ViewModel();

        Assert.True(vm.KeyboardPresent);
        Assert.Contains("ENG-UK", vm.DeviceLine);
    }

    // ---- Presets --------------------------------------------------------------------

    [Fact]
    public void The_list_starts_with_the_built_ins_followed_by_the_saved_ones()
    {
        var h = Build();
        h.Saved.Presets.Add(new LightingPreset { Name = "Mine" });

        var vm = h.ViewModel();

        Assert.Equal(
            LightingSettings.BuiltInPresets.Select(p => p.Name).Append("Mine"),
            vm.Presets.Select(p => p.Name));
    }

    /// <summary>
    /// <see cref="LightingSettings.BuiltInPresets"/> hands out fresh instances on every access
    /// precisely so the panel can bind them to editable fields. Caching them in a static field
    /// here would undo that without the settings-level test noticing, because that test checks
    /// the source and not this cache - so this one asks a second view model what it starts with.
    /// </summary>
    [Fact]
    public void Editing_an_exposed_preset_does_not_rewrite_the_built_in()
    {
        var h = Build();
        var first = h.ViewModel();

        var off = first.Presets.Single(p => p.Name == "Off");
        off.Name = "Renamed";
        off.BrightnessPercent = 99;
        off.Color = new RgbColor(1, 2, 3);

        Assert.Equal("Off", LightingSettings.BuiltInPresets[0].Name);

        var second = h.ViewModel();
        Assert.Equal("Off", second.Presets[0].Name);
        Assert.Equal(0, second.Presets[0].BrightnessPercent);
        Assert.Equal(RgbColor.Black, second.Presets[0].Color);
    }

    [Fact]
    public async Task Applying_a_preset_copies_its_parameters_in_and_writes_them()
    {
        var h = Build();
        var preset = new LightingPreset
        {
            Name = "Mine",
            Effect = LightEffect.Wave,
            Color = new RgbColor(0x20, 0x30, 0x40),
            SpeedPercent = 90,
            BrightnessPercent = 77,
            Direction = LightDirection.Left,
            Random = true,
        };
        h.Saved.Presets.Add(preset);
        var vm = h.ViewModel();

        await vm.ApplyPresetCommand.ExecuteAsync(preset);

        Assert.Equal(LightEffect.Wave, vm.SelectedEffect);
        Assert.Equal(MediaColor.FromRgb(0x20, 0x30, 0x40), vm.PickedColor);
        Assert.Equal(90, vm.SpeedPercent);
        Assert.Equal(77, vm.BrightnessPercent);
        Assert.Equal(LightDirection.Left, vm.Direction);
        Assert.True(vm.Random);

        // One sequence, not one per property copied in.
        Assert.Equal(2, h.Hid.Written.Count);
        var report = Assert.Single(h.EffectReports);
        Assert.Equal((byte)LightEffect.Wave, report[10]);
        Assert.Equal(77, report[12]);
        Assert.Contains("Mine", vm.StatusText);

        // The preset itself is a source, not a scratchpad.
        Assert.Equal("Mine", preset.Name);
        Assert.Equal(new RgbColor(0x20, 0x30, 0x40), preset.Color);
        Assert.Equal(77, preset.BrightnessPercent);
    }

    [Fact]
    public async Task Applying_a_per_key_preset_writes_the_colours_then_selects_custom()
    {
        var h = Build();
        var preset = new LightingPreset
        {
            Name = "Painted",
            Effect = LightEffect.Static,
            BrightnessPercent = 61,
            PerKeyColors = Slots(new RgbColor(0x0F, 0x1E, 0x2D)),
        };
        h.Saved.Presets.Add(preset);
        var vm = h.ViewModel();

        await vm.ApplyPresetCommand.ExecuteAsync(preset);

        Assert.Equal(4, h.Hid.Written.Count);
        Assert.Equal(0x06, h.Hid.Command(0));
        Assert.Equal(0x06, h.Hid.Command(1));
        Assert.Equal(0x82, h.Hid.Command(2));
        Assert.Equal(0x02, h.Hid.Command(3));
        Assert.Equal((byte)LightEffect.Custom, h.Hid.Written[3][10]);
        Assert.Equal(61, h.Hid.Written[3][12]);
        Assert.Equal(LightEffect.Custom, vm.SelectedEffect);

        var reloaded = h.Reload().Lighting;
        Assert.Equal(LightEffect.Custom, reloaded.Effect);
        Assert.Equal(61, reloaded.BrightnessPercent);
        Assert.Equal(preset.PerKeyColors, reloaded.PerKeyColors);
        Assert.Equal(KeyLayout.SlotCount, preset.PerKeyColors!.Count); // and the preset kept its own list
    }

    /// <summary>
    /// The same rule as for an effect write, on the path that takes longest to finish: a per-key
    /// preset is four paced reports, so the window closing part-way through one is an ordinary
    /// shape rather than a rare race. Without the catch the throw comes out of an
    /// <c>AsyncRelayCommand</c>, which does not flow exceptions to the task scheduler by
    /// default, so it surfaces as an unhandled UI-thread exception on the way out.
    /// </summary>
    [Fact]
    public async Task Shutdown_abandons_a_per_key_preset_without_reporting_it()
    {
        var gate = new SemaphoreSlim(0);
        var h = Build(delay: async _ => await gate.WaitAsync());
        var preset = new LightingPreset
        {
            Name = "Painted",
            BrightnessPercent = 40,
            PerKeyColors = Slots(RgbColor.White),
        };
        h.Saved.Presets.Add(preset);
        var vm = h.ViewModel();

        var applying = vm.ApplyPresetCommand.ExecuteAsync(preset);
        vm.Shutdown();     // the window is closing mid-sequence
        gate.Release(8);
        await applying;    // must not throw

        Assert.Empty(h.Banners);
        Assert.DoesNotContain(h.Hid.Written, r => r[1] == 0x02); // stopped before custom was selected
        Assert.NotEqual("Failed", vm.StatusText);
    }

    /// <summary>
    /// A click on a preset lands within a pacing delay of the slider the owner has just let go
    /// of, which is an ordinary gesture. That slider's value is already waiting in the latch
    /// when the command starts, and suppressing the property copies does nothing about it: the
    /// per-key sequence runs outside the latch, so the stranded value's write queues on the
    /// controller's gate behind it and lands last - panel saying Custom, keyboard back in the
    /// superseded effect, settings agreeing with the keyboard, and no banner to explain it.
    /// </summary>
    [Fact]
    public async Task A_per_key_preset_supersedes_a_value_still_waiting_in_the_latch()
    {
        var gate = new SemaphoreSlim(0);
        var h = Build(delay: async _ => await gate.WaitAsync());
        var preset = new LightingPreset
        {
            Name = "Painted",
            Effect = LightEffect.Static,
            BrightnessPercent = 61,
            PerKeyColors = Slots(new RgbColor(0x0F, 0x1E, 0x2D)),
        };
        h.Saved.Presets.Add(preset);
        var vm = h.ViewModel();

        vm.SelectedEffect = LightEffect.Wave;  // starts a write, which parks on the pacing delay
        vm.BrightnessPercent = 22;             // waits in the latch behind it
        var applying = vm.ApplyPresetCommand.ExecuteAsync(preset);

        gate.Release(32);
        await applying;
        await vm.LiveWrites;

        // Custom is where the keyboard ends up, and the colours it ends up with are the preset's.
        Assert.Equal(LightEffect.Custom, vm.SelectedEffect);
        var last = h.EffectReports[^1];
        Assert.Equal((byte)LightEffect.Custom, last[10]);
        Assert.Equal(61, last[12]);
        Assert.Equal(0x06, h.Hid.Command(h.Hid.Written.Count - 4)); // the colour pages ran last, not first

        var reloaded = h.Reload().Lighting;
        Assert.Equal(LightEffect.Custom, reloaded.Effect);
        Assert.Equal(preset.PerKeyColors, reloaded.PerKeyColors);
        Assert.Empty(h.Banners);
    }

    [Fact]
    public void Saving_a_preset_adds_it_to_the_list_and_to_the_file()
    {
        var h = Build();
        var vm = h.ViewModel();
        vm.SelectedEffect = LightEffect.Ripple;
        vm.PickedColor = MediaColor.FromRgb(0x90, 0xA0, 0xB0);
        vm.SpeedPercent = 35;
        vm.BrightnessPercent = 45;

        vm.SavePresetCommand.Execute(null);

        var added = vm.Presets[^1];
        var saved = Assert.Single(h.Reload().Lighting.Presets);
        Assert.Equal(added.Name, saved.Name);
        Assert.Equal(LightEffect.Ripple, saved.Effect);
        Assert.Equal(new RgbColor(0x90, 0xA0, 0xB0), saved.Color);
        Assert.Equal(35, saved.SpeedPercent);
        Assert.Equal(45, saved.BrightnessPercent);
        Assert.Null(saved.PerKeyColors);
    }

    [Fact]
    public void Saving_twice_does_not_reuse_a_name()
    {
        var vm = Build().ViewModel();

        vm.SavePresetCommand.Execute(null);
        vm.SavePresetCommand.Execute(null);
        vm.DeletePresetCommand.Execute(vm.Presets[^2]);
        vm.SavePresetCommand.Execute(null);

        var names = vm.Presets.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Deleting_a_saved_preset_removes_it_from_the_list_and_the_file()
    {
        var h = Build();
        var vm = h.ViewModel();
        vm.SavePresetCommand.Execute(null);
        var mine = vm.Presets[^1];

        vm.DeletePresetCommand.Execute(mine);

        Assert.DoesNotContain(mine, vm.Presets);
        Assert.Empty(h.Reload().Lighting.Presets);
    }

    [Fact]
    public void A_built_in_preset_cannot_be_deleted()
    {
        var h = Build();
        var vm = h.ViewModel();
        var builtIn = vm.Presets[0];

        vm.DeletePresetCommand.Execute(builtIn);

        Assert.Contains(builtIn, vm.Presets);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusText));
    }

    // ---- The colour boxes -----------------------------------------------------------

    [Fact]
    public void The_hex_boxes_and_the_picked_colours_follow_each_other()
    {
        var vm = Build().ViewModel();

        vm.PickedColor = MediaColor.FromRgb(0x12, 0x34, 0x56);
        vm.PickedSecondColor = MediaColor.FromRgb(0x65, 0x43, 0x21);

        Assert.Equal("#123456", vm.ColorHex);
        Assert.Equal("#654321", vm.SecondColorHex);

        vm.ColorHex = "abcdef";
        Assert.Equal(MediaColor.FromRgb(0xAB, 0xCD, 0xEF), vm.PickedColor);
    }

    /// <summary>
    /// The hex box is bound live, so it sees "#A", "#AB", "#ABC" on the way to a whole colour.
    /// Each of those reaching the keyboard would be a write of something the owner never chose.
    /// </summary>
    [Fact]
    public async Task A_half_typed_hex_value_writes_nothing()
    {
        var h = Build();
        var vm = h.ViewModel();
        vm.PickedColor = MediaColor.FromRgb(0x12, 0x34, 0x56);
        await vm.LiveWrites;
        var before = h.Hid.Written.Count;

        vm.ColorHex = "#AB";
        await vm.LiveWrites;

        Assert.Equal(MediaColor.FromRgb(0x12, 0x34, 0x56), vm.PickedColor);
        Assert.Equal(before, h.Hid.Written.Count);
    }

    // ---- The per-key editor ---------------------------------------------------------

    [Fact]
    public void The_panel_owns_a_per_key_editor_over_the_same_layout()
    {
        var h = Build();

        var vm = h.ViewModel();

        Assert.Equal(
            h.Services.Lighting.Layout.RealKeys.Select(k => k.Slot),
            vm.PerKey.Keys.Select(k => k.Slot).OrderBy(s => s));
    }

    /// <summary>
    /// A per-key write carries brightness with it, and the panel's slider is the live value -
    /// the settings only catch up once a write has landed.
    /// </summary>
    [Fact]
    public async Task The_editor_writes_at_the_panel_s_current_brightness()
    {
        var h = Build();
        var vm = h.ViewModel();
        vm.BrightnessPercent = 88;
        await vm.LiveWrites;
        h.Hid.ClearWritten();

        await vm.PerKey.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(88, h.Hid.Written[^1][12]);
        Assert.Equal((byte)LightEffect.Custom, h.Hid.Written[^1][10]);
    }

    /// <summary>
    /// Shutdown reaches the editor too: a per-key sequence is four paced reports, and there is
    /// no more window to show its result on than there is for an effect write.
    /// </summary>
    [Fact]
    public async Task Shutdown_abandons_a_per_key_write_without_reporting_it()
    {
        var gate = new SemaphoreSlim(0);
        var h = Build(delay: async _ => await gate.WaitAsync());
        var vm = h.ViewModel();

        var applying = vm.PerKey.ApplyCommand.ExecuteAsync(null);
        vm.Shutdown();
        gate.Release(8);
        await applying;  // must not throw

        Assert.Empty(h.Banners);
        Assert.DoesNotContain(h.Hid.Written, r => r[1] == 0x02); // stopped before custom was selected
    }
}
