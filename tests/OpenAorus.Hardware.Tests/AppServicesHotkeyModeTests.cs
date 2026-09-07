using System.IO;
using OpenAorus.App;
using OpenAorus.App.Hotkeys;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Which mode the fan key cycles from, on a machine the watchdog has taken over.
/// </summary>
/// <remarks>
/// <para>
/// The saved mode and the running mode are not the same thing, and they come apart exactly when
/// it matters most: the thermal watchdog forces Turbo without writing that to settings.json - on
/// purpose, so a forced Turbo is not what the machine boots into next time - so
/// <c>settings.Mode</c> still says whatever the owner last chose while the fans are at full.
/// </para>
/// <para>
/// A fan key that reads the saved mode therefore cycles from the wrong place on a hot machine:
/// from a saved Quiet it applies Normal, taking a CPU at the trigger temperature <em>down</em>
/// from full. That apply re-arms the watchdog through <see cref="FanController.Applied"/>, the
/// next poll sees the same hot CPU and forces Turbo again, and the machine flaps. From a saved
/// Turbo the first press lands on Quiet at 90 °C. So the cursor the hotkeys read has to follow
/// the controller, not the file.
/// </para>
/// </remarks>
public class AppServicesHotkeyModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>The reading that fires the watchdog: at the trigger temperature, fans doing little.</summary>
    private static SensorSnapshot Hot() => new(FanSafety.WatchdogTriggerTemperature, 50, 3000, 3000, 10, 10, true, null);

    private (MainViewModel vm, AppSettings settings, Func<FanMode> cursor) Make(FanMode saved)
    {
        var profile = ModelProfile.Detect("AORUS 17G KD");
        var wmi = new FakeGigabyteWmi();
        var settings = new AppSettings { Mode = saved };
        var fans = new FanController(wmi, profile, delay: _ => Task.CompletedTask);
        var cursor = AppServices.TrackAppliedMode(fans, settings.Mode);

        var vm = new MainViewModel(new AppServices
        {
            Profile = profile,
            Wmi = wmi,
            Fans = fans,
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = new SettingsStore(Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json")),
            Settings = settings,
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(new FakeKeyboardHid(), KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = false,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        });
        return (vm, settings, cursor);
    }

    [Fact]
    public async Task A_forced_turbo_moves_the_mode_the_fan_key_reads()
    {
        var (vm, settings, cursor) = Make(saved: FanMode.Quiet);
        Assert.Equal(FanMode.Quiet, cursor());

        await vm.OnSensorPollAsync(Hot());

        // The window agrees the machine is at full, and so does the cursor the hotkeys read ...
        Assert.Equal(FanMode.Turbo, vm.SelectedMode);
        Assert.Equal(FanMode.Turbo, cursor());
        // ... while the saved mode is deliberately untouched, which is the whole reason the two
        // must not be the same reading.
        Assert.Equal(FanMode.Quiet, settings.Mode);
    }

    [Fact]
    public async Task A_press_on_a_machine_the_watchdog_took_over_cycles_from_full()
    {
        var (vm, settings, cursor) = Make(saved: FanMode.Quiet);
        await vm.OnSensorPollAsync(Hot());

        // The whole path a real press takes, over the cursor the app hands the service.
        var raw = new FakeHotkeySource();
        using var hotkeys = new HotkeyService(
            raw, new FakeWmiEventSource(), settings.Hotkeys, cursor,
            post: w => w(), clock: () => 0);
        var running = new List<Task>();
        hotkeys.ActionRequested += a => running.Add(vm.OnHotkeyAsync(a));
        hotkeys.Start();

        raw.Emit(4, 0, 0, 39);   // the fan key, as the research reads it
        await Task.WhenAll(running);

        // Turbo cycles to Quiet - the owner asked for that, with the machine at 90 °C and the
        // watchdog free to take it straight back. What must not happen is Normal, which is where a
        // press read off the stale saved Quiet would have gone: the app moving a hot machine down
        // from full without anyone asking, and then flapping back to Turbo on the next poll.
        Assert.Equal(FanMode.Quiet, vm.SelectedMode);
    }
}
