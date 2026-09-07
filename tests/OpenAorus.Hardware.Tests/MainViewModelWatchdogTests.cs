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

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The half of the watchdog <see cref="FanWatchdogTests"/> cannot see: what the view model
/// actually does with the decision - the write it sends, what it tells the owner, and where it
/// keeps quiet.
/// </summary>
/// <remarks>
/// Driven a poll at a time through <see cref="MainViewModel.OnSensorPollAsync"/> rather than
/// through <see cref="SensorPoller"/>, which needs a dispatcher and a real clock. The poller's
/// only job is to call that method; everything worth pinning is on this side of it.
/// </remarks>
public class MainViewModelWatchdogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>Hot enough to qualify, with room above the trigger.</summary>
    private const int Hot = 97;

    /// <summary>Where the owner's machine actually sits while the fans are roaring for no reason.</summary>
    private const int Cool = 60;

    private (MainViewModel vm, FakeGigabyteWmi wmi, AppServices services) Make(
        ModelProfile? profile = null, FanMode saved = FanMode.Normal)
    {
        var p = profile ?? ModelProfile.Detect("AORUS 17G KD");
        var wmi = new FakeGigabyteWmi();
        var services = new AppServices
        {
            Profile = p,
            Wmi = wmi,
            Fans = new FanController(wmi, p, delay: _ => Task.CompletedTask),
            Sensors = new SensorReader(wmi, p),
            Battery = new BatteryController(wmi),
            Store = new SettingsStore(Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json")),
            Settings = new AppSettings { Mode = saved },
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(new FakeKeyboardHid(), KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = false,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        };
        return (new MainViewModel(services), wmi, services);
    }

    private static SensorSnapshot Poll(int cpu, int duty, bool ok = true) =>
        new(cpu, 50, 3000, 3000, duty, duty, ok, ok ? null : "getCpuTemp: failed (fake)");

    /// <summary>Runs the machine hot for exactly as long as the watchdog now insists on before it
    /// will force anything.</summary>
    private static async Task HotSpellAsync(MainViewModel vm, int cpu = Hot, int duty = 10)
    {
        for (var i = 0; i < FanSafety.WatchdogPollsToFire; i++)
            await vm.OnSensorPollAsync(Poll(cpu, duty));
    }

    /// <summary>Runs the machine cool for exactly as long as it takes to earn the fans back.</summary>
    private static async Task CoolSpellAsync(MainViewModel vm, int cpu = Cool, int duty = 100)
    {
        for (var i = 0; i < FanSafety.WatchdogPollsToRelease; i++)
            await vm.OnSensorPollAsync(Poll(cpu, duty));
    }

    /// <summary>Whether the Gaming write sequence went out - the aggressive automatic curve is the
    /// one mode that sets SetAutoFanStatus to 1.</summary>
    private static bool RaisedFans(FakeGigabyteWmi wmi) =>
        wmi.Calls.Any(c => c.Method == "SetAutoFanStatus" && c.Data == 1);

    /// <summary>Whether the Turbo write sequence went out - fans pinned at the profile's DutyMax
    /// with SetFixedFanStatus latched.</summary>
    private static bool PinnedFansAtMaximum(FakeGigabyteWmi wmi) =>
        wmi.Calls.Any(c => c.Method == "SetFixedFanSpeed" && c.Data == 229)
        && wmi.Calls.Any(c => c.Method == "SetFixedFanStatus" && c.Data == 1);

    [Fact]
    public async Task A_cpu_held_at_the_trigger_with_slow_fans_is_first_put_on_the_aggressive_curve()
    {
        var (vm, wmi, _) = Make();

        await HotSpellAsync(vm);

        // Gaming, not Turbo: it still tracks temperature and backs off as the machine cools,
        // where Turbo is a fixed duty that stops responding to the sensor entirely.
        Assert.True(RaisedFans(wmi));
        Assert.False(PinnedFansAtMaximum(wmi));
        Assert.Equal(BannerKind.Warning, vm.Banner);
        Assert.Contains("Fans raised", vm.BannerText);
        Assert.Contains("97", vm.BannerText);
        // The window has to agree with the machine: it really is in Gaming now.
        Assert.Equal(FanMode.Gaming, vm.SelectedMode);
    }

    [Fact]
    public async Task A_momentary_spike_never_reaches_the_controller()
    {
        var (vm, wmi, _) = Make();

        // The owner's complaint, in one test. One or two polls in the 90s is what this CPU does
        // all day; it is not a reason to pin the fans at anything.
        for (var i = 0; i < FanSafety.WatchdogPollsToFire - 1; i++)
            await vm.OnSensorPollAsync(Poll(cpu: 110, duty: 10));
        await vm.OnSensorPollAsync(Poll(cpu: Cool, duty: 30));

        Assert.Empty(wmi.Calls);
        Assert.Equal(BannerKind.None, vm.Banner);
        Assert.Equal(FanMode.Normal, vm.SelectedMode);
    }

    [Fact]
    public async Task A_machine_the_aggressive_curve_did_not_cool_is_then_pinned_at_maximum()
    {
        var (vm, wmi, _) = Make();
        await HotSpellAsync(vm);
        wmi.Calls.Clear();

        // Gaming has landed and the CPU fan is still reading below the duty floor, so whatever
        // that curve does up here, it is not doing it here. Last resort.
        await vm.OnSensorPollAsync(Poll(cpu: 99, duty: 10));

        Assert.True(PinnedFansAtMaximum(wmi));
        Assert.Equal(FanMode.Turbo, vm.SelectedMode);
        Assert.Equal(BannerKind.Warning, vm.Banner);
        Assert.Contains("Fans forced to maximum", vm.BannerText);
        Assert.Contains("99", vm.BannerText);
    }

    [Fact]
    public async Task The_two_stages_say_different_things()
    {
        var (vm, _, _) = Make();

        await HotSpellAsync(vm);
        var raised = vm.BannerText;
        var raisedStatus = vm.StatusLine;

        await vm.OnSensorPollAsync(Poll(cpu: 99, duty: 10));

        // "The fans were turned up" and "the fans are at maximum until the machine cools" are
        // different pieces of news, and the owner has to be able to tell which one they got.
        Assert.NotEqual(raised, vm.BannerText);
        Assert.NotEqual(raisedStatus, vm.StatusLine);
        Assert.DoesNotContain("maximum", raised);
        Assert.Contains("maximum", vm.BannerText);
    }

    [Fact]
    public async Task A_machine_the_aggressive_curve_did_cool_is_left_on_it()
    {
        var (vm, wmi, _) = Make();
        await HotSpellAsync(vm);
        wmi.Calls.Clear();

        // Still hot, but the fans are now doing the work. Pinning them at maximum would buy
        // nothing and cost the temperature tracking.
        await vm.OnSensorPollAsync(Poll(cpu: 99, duty: FanSafety.WatchdogDutyFloor));
        await vm.OnSensorPollAsync(Poll(cpu: 99, duty: 100));

        Assert.Empty(wmi.Calls);
        Assert.Equal(FanMode.Gaming, vm.SelectedMode);
    }

    [Fact]
    public async Task It_does_not_re_apply_a_mode_on_every_poll_while_the_machine_stays_hot()
    {
        var (vm, wmi, _) = Make();

        // Half a minute of a machine at 97 °C whose fans never come up. The two stages are the
        // only write sequences allowed out in that time - anything more is the per-second
        // re-drive the latch exists to prevent.
        for (var poll = 0; poll < 30; poll++)
            await vm.OnSensorPollAsync(Poll(cpu: Hot, duty: 10));

        Assert.Equal(1, wmi.Calls.Count(c => c.Method == "SetAutoFanStatus" && c.Data == 1));
        Assert.Equal(1, wmi.Calls.Count(c => c.Method == "SetFixedFanSpeed" && c.Data == 229));
        Assert.Equal(FanMode.Turbo, vm.SelectedMode);
    }

    [Fact]
    public async Task The_notice_survives_the_polls_that_follow_it()
    {
        var (vm, _, _) = Make();
        await HotSpellAsync(vm);
        await vm.OnSensorPollAsync(Poll(cpu: 99, duty: 10));

        await vm.OnSensorPollAsync(Poll(cpu: 70, duty: 100));
        await vm.OnSensorPollAsync(Poll(cpu: 55, duty: 40));

        // Two cool polls are not the fifteen it takes to hand the fans back, so the fans are
        // still at maximum - and nothing may quietly stop saying so while they are.
        Assert.Contains("Fans forced to maximum", vm.BannerText);
    }

    [Fact]
    public async Task A_failed_sensor_read_showing_as_zero_degrees_does_nothing()
    {
        var (vm, wmi, _) = Make();

        for (var poll = 0; poll < 30; poll++)
            await vm.OnSensorPollAsync(Poll(cpu: 0, duty: 0, ok: false));

        Assert.Empty(wmi.Calls);
        Assert.DoesNotContain("Fans raised", vm.BannerText);
        Assert.DoesNotContain("forced to maximum", vm.BannerText);
    }

    [Fact]
    public async Task A_cool_machine_is_left_alone()
    {
        var (vm, wmi, _) = Make();

        for (var poll = 0; poll < 30; poll++)
            await vm.OnSensorPollAsync(Poll(cpu: 62, duty: 30));

        // Never held, so there is nothing to hand back either - a machine that was always cool
        // must not have a mode written to it just for being cool.
        Assert.Empty(wmi.Calls);
        Assert.Equal(BannerKind.None, vm.Banner);
    }

    [Fact]
    public async Task A_machine_already_cooling_itself_is_left_alone()
    {
        var (vm, wmi, _) = Make();

        for (var poll = 0; poll < 30; poll++)
            await vm.OnSensorPollAsync(Poll(cpu: 99, duty: 100));

        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public async Task On_a_model_it_cannot_write_to_it_neither_acts_nor_complains()
    {
        // The WMI writes are withheld here, so there is nothing the watchdog could do about the
        // temperature - and a warning once a second on top of the banner that already explains
        // the model would be noise, not help.
        var (vm, wmi, _) = Make(ModelProfile.Detect("Some Other Laptop"));

        await HotSpellAsync(vm, cpu: 99, duty: 0);
        await HotSpellAsync(vm, cpu: 99, duty: 0);
        await CoolSpellAsync(vm);

        Assert.Empty(wmi.Calls);
        Assert.DoesNotContain("Fans raised", vm.BannerText);
        Assert.DoesNotContain("forced to maximum", vm.BannerText);
        Assert.DoesNotContain("handed back", vm.BannerText);
        Assert.Equal(BannerKind.Error, vm.Banner);   // still the read-only model banner
    }

    [Fact]
    public async Task A_failed_write_is_reported_rather_than_swallowed()
    {
        var (vm, wmi, _) = Make();
        wmi.FailOn.Add("SetAutoFanStatus");   // in every sequence either stage would send

        await HotSpellAsync(vm);

        Assert.Equal(BannerKind.Error, vm.Banner);
        Assert.Contains("SetAutoFanStatus", vm.BannerText);
    }

    [Fact]
    public async Task A_failed_stage_leaves_the_window_showing_the_mode_the_machine_is_still_in()
    {
        var (vm, wmi, _) = Make();
        wmi.FailOn.Add("SetAutoFanStatus");
        var before = vm.SelectedMode;

        await HotSpellAsync(vm);

        Assert.Equal(before, vm.SelectedMode);
    }

    [Fact]
    public async Task Re_applying_the_saved_mode_re_arms_it_even_though_the_machine_never_cooled()
    {
        var (vm, wmi, services) = Make();
        await HotSpellAsync(vm);

        // What a resume does: the owner's saved mode goes back on, undoing what the watchdog
        // forced. The CPU never dropped below the re-arm point across the sleep, so the
        // temperature rule alone would leave the machine back on a slow mode, still hot, with the
        // guard latched shut.
        await services.ApplySavedAsync();
        wmi.Calls.Clear();

        await vm.OnSensorPollAsync(Poll(cpu: 99, duty: 10));

        // Re-armed, and re-armed to the beginning: the escalation starts over from the stage that
        // still tracks temperature, not from the one that stopped.
        Assert.True(RaisedFans(wmi));
        Assert.False(PinnedFansAtMaximum(wmi));
        Assert.Equal(FanMode.Gaming, vm.SelectedMode);
    }

    [Fact]
    public async Task A_write_that_failed_is_tried_again_on_the_next_poll()
    {
        var (vm, wmi, _) = Make();
        wmi.FailOn.Add("SetAutoFanStatus");

        await HotSpellAsync(vm);
        wmi.Calls.Clear();
        wmi.FailOn.Clear();

        await vm.OnSensorPollAsync(Poll(cpu: 98, duty: 10));

        // The first stage never reached the controller, so the retry is the first stage again -
        // there is nothing yet to escalate away from - and it is the very next poll, not five
        // polls later, because the machine already proved itself sustained.
        Assert.True(RaisedFans(wmi));
        Assert.Contains("Fans raised", vm.BannerText);
    }

    [Fact]
    public async Task A_write_that_keeps_failing_is_retried_without_a_fresh_notice_each_poll()
    {
        var (vm, wmi, _) = Make();
        wmi.FailOn.Add("SetAutoFanStatus");

        await HotSpellAsync(vm);                                  // the run, ending in the first attempt
        await vm.OnSensorPollAsync(Poll(cpu: 98, duty: 10));
        await vm.OnSensorPollAsync(Poll(cpu: 99, duty: 10));

        // Every poll past the run retries - that is the point of not latching on a failure ...
        Assert.Equal(3, wmi.Calls.Count(c => c.Method == "SetAutoFanStatus"));
        // ... but the owner is told once. The banner still carries the first report, not a
        // rewrite from a second later naming a temperature one degree different.
        Assert.Equal(BannerKind.Error, vm.Banner);
        Assert.Contains("97", vm.BannerText);
        Assert.DoesNotContain("98", vm.BannerText);
        Assert.DoesNotContain("99", vm.BannerText);
    }

    [Fact]
    public async Task It_decides_on_the_raw_reading_and_not_on_the_number_the_window_shows()
    {
        var (vm, wmi, _) = Make();

        // Fill the display's window with a cool machine, then get genuinely hot. The average is
        // still weighed down by the cool samples that have not walked out of the window yet.
        for (var i = 0; i < TemperatureAverage.DisplaySamples; i++)
            await vm.OnSensorPollAsync(Poll(cpu: Cool, duty: 30));

        await HotSpellAsync(vm);

        // Smoothing what the owner sees is cosmetic; smoothing what the safety logic decides on
        // would be hiding things. If the smoothed number were what reached the watchdog, this
        // machine would still be uncooled - it has not even reached the trigger on screen.
        Assert.True(RaisedFans(wmi));
        Assert.True(vm.DisplayCpuTemp < FanSafety.WatchdogTriggerTemperature,
            $"the displayed average is {vm.DisplayCpuTemp} °C and must not be what fired this");

        // And what is written down about the machine stays what the machine actually said.
        Assert.Equal(Hot, vm.Sensors.CpuTemp);
        Assert.Contains(Hot.ToString(), vm.BannerText);
    }

    // ---- handing the fans back --------------------------------------------------------------

    [Fact]
    public async Task A_machine_that_has_cooled_gets_the_owners_own_mode_back()
    {
        var (vm, wmi, _) = Make(saved: FanMode.Quiet);
        await HotSpellAsync(vm);
        Assert.Equal(FanMode.Gaming, vm.SelectedMode);
        wmi.Calls.Clear();

        await CoolSpellAsync(vm);

        // Quiet, which is what the owner picked - not Gaming, which is where the watchdog moved
        // the selection. Reading the selection back here would hand the machine its own override.
        Assert.Equal(FanMode.Quiet, vm.SelectedMode);
        Assert.NotEmpty(wmi.Calls);
        Assert.Contains("handed back", vm.BannerText);
        Assert.Contains("Quiet", vm.BannerText);
        Assert.Contains("Quiet", vm.StatusLine);
    }

    [Fact]
    public async Task The_last_resort_is_handed_back_too()
    {
        var (vm, wmi, _) = Make(saved: FanMode.Quiet);
        await HotSpellAsync(vm);
        await vm.OnSensorPollAsync(Poll(cpu: 99, duty: 10));
        Assert.Equal(FanMode.Turbo, vm.SelectedMode);
        wmi.Calls.Clear();

        // The owner's actual report: a spike pins the fans at maximum, the machine is at 60 °C,
        // and Turbo does not read the sensor - so nothing but this brings them down.
        await CoolSpellAsync(vm);

        Assert.Equal(FanMode.Quiet, vm.SelectedMode);
        Assert.Contains("handed back", vm.BannerText);
    }

    [Fact]
    public async Task The_fans_are_not_handed_back_before_the_machine_has_earned_it()
    {
        var (vm, wmi, _) = Make(saved: FanMode.Quiet);
        await HotSpellAsync(vm);
        wmi.Calls.Clear();

        for (var poll = 0; poll < FanSafety.WatchdogPollsToRelease - 1; poll++)
            await vm.OnSensorPollAsync(Poll(cpu: Cool, duty: 100));

        Assert.Empty(wmi.Calls);
        Assert.Equal(FanMode.Gaming, vm.SelectedMode);
    }

    [Fact]
    public async Task Handing_the_fans_back_happens_once_and_does_not_start_another_cycle()
    {
        var (vm, wmi, _) = Make(saved: FanMode.Quiet);
        await HotSpellAsync(vm);
        wmi.Calls.Clear();

        // Two minutes of a cool machine. Restoring the owner's mode is itself an apply and an
        // apply re-arms the watchdog, so this is the shape that could have looped: exactly one
        // hand-back write sequence is allowed out, and then nothing.
        for (var poll = 0; poll < 120; poll++)
            await vm.OnSensorPollAsync(Poll(cpu: Cool, duty: 100));

        Assert.Equal(1, wmi.Calls.Count(c => c.Method == "SetFixedFanStatus" && c.Data == 0));
        Assert.Equal(FanMode.Quiet, vm.SelectedMode);
    }

    [Fact]
    public async Task A_machine_that_gets_hot_again_after_a_hand_back_is_guarded_again()
    {
        var (vm, wmi, _) = Make(saved: FanMode.Quiet);
        await HotSpellAsync(vm);
        await CoolSpellAsync(vm);
        Assert.Equal(FanMode.Quiet, vm.SelectedMode);
        wmi.Calls.Clear();

        await HotSpellAsync(vm);

        // Letting go is not switching off. The next emergency is a whole new one and starts at
        // the gentler stage.
        Assert.True(RaisedFans(wmi));
        Assert.False(PinnedFansAtMaximum(wmi));
        Assert.Equal(FanMode.Gaming, vm.SelectedMode);
    }

    [Fact]
    public async Task A_hand_back_that_failed_is_reported_and_leaves_the_window_honest()
    {
        var (vm, wmi, _) = Make(saved: FanMode.Quiet);
        await HotSpellAsync(vm);
        wmi.FailOn.Add("SetFixedFanStatus");   // the first write Quiet's sequence sends

        await CoolSpellAsync(vm);

        // The machine is still in whatever the watchdog put it in, so the window has to keep
        // saying so - and the owner has to be told their mode did not go back on.
        Assert.Equal(FanMode.Gaming, vm.SelectedMode);
        Assert.Equal(BannerKind.Error, vm.Banner);
        Assert.Contains("SetFixedFanStatus", vm.BannerText);
    }
}
