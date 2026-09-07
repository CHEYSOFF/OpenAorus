using System.IO;
using OpenAorus.App;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The two temperatures on the window, which are smoothed, and the two on
/// <see cref="MainViewModel.Sensors"/>, which are not.
/// </summary>
/// <remarks>
/// Both exist because they answer different questions. "How hot is this machine" is a question a
/// person asks of a number they can read, and a field redrawn from a raw sample once a second is
/// not one. "What did the sensor say on this poll" is what the watchdog, the notices it writes and
/// anything recording the machine need, and there the average would be a quiet lie.
/// </remarks>
public class MainViewModelDisplayTemperatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private MainViewModel Make()
    {
        var p = ModelProfile.Detect("AORUS 17G KD");
        var wmi = new FakeGigabyteWmi();
        return new MainViewModel(new AppServices
        {
            Profile = p,
            Wmi = wmi,
            Fans = new FanController(wmi, p, delay: _ => Task.CompletedTask),
            Sensors = new SensorReader(wmi, p),
            Battery = new BatteryController(wmi),
            Store = new SettingsStore(Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json")),
            Settings = new AppSettings(),
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(new FakeKeyboardHid(), KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = false,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        });
    }

    private static SensorSnapshot Poll(int cpu, int gpu, bool ok = true) =>
        new(cpu, gpu, 3000, 3000, 100, 100, ok, ok ? null : "getCpuTemp: failed (fake)");

    [Fact]
    public async Task The_first_reading_is_shown_as_it_is()
    {
        var vm = Make();

        await vm.OnSensorPollAsync(Poll(cpu: 55, gpu: 45));

        // Nothing to average with yet, and an owner opening the window should not be shown a
        // number that has to warm up.
        Assert.Equal(55, vm.DisplayCpuTemp);
        Assert.Equal(45, vm.DisplayGpuTemp);
    }

    [Fact]
    public async Task A_single_spike_does_not_throw_the_number_across_the_dial()
    {
        var vm = Make();
        for (var i = 0; i < TemperatureAverage.DisplaySamples; i++)
            await vm.OnSensorPollAsync(Poll(cpu: 60, gpu: 50));

        await vm.OnSensorPollAsync(Poll(cpu: 100, gpu: 50));

        // The owner's actual complaint about the display: this CPU boosts to 100 °C for one poll
        // constantly, and a field following it exactly is a flicker rather than a reading.
        Assert.InRange(vm.DisplayCpuTemp, 60, 75);
        // ... while the reading itself is untouched, because that is what the rest of the app uses.
        Assert.Equal(100, vm.Sensors.CpuTemp);
    }

    [Fact]
    public async Task It_still_follows_the_machine()
    {
        var vm = Make();
        for (var i = 0; i < TemperatureAverage.DisplaySamples; i++)
            await vm.OnSensorPollAsync(Poll(cpu: 50, gpu: 40));
        Assert.Equal(50, vm.DisplayCpuTemp);

        for (var i = 0; i < TemperatureAverage.DisplaySamples; i++)
            await vm.OnSensorPollAsync(Poll(cpu: 85, gpu: 75));

        // Smoothed, not stuck. A window's worth of a hotter machine and the number is there.
        Assert.Equal(85, vm.DisplayCpuTemp);
        Assert.Equal(75, vm.DisplayGpuTemp);
    }

    [Fact]
    public async Task The_two_temperatures_are_averaged_apart()
    {
        var vm = Make();

        await vm.OnSensorPollAsync(Poll(cpu: 90, gpu: 40));
        await vm.OnSensorPollAsync(Poll(cpu: 90, gpu: 40));

        Assert.Equal(90, vm.DisplayCpuTemp);
        Assert.Equal(40, vm.DisplayGpuTemp);
    }

    [Fact]
    public async Task A_dropped_read_does_not_make_the_number_dive_to_zero()
    {
        var vm = Make();
        await vm.OnSensorPollAsync(Poll(cpu: 70, gpu: 60));

        await vm.OnSensorPollAsync(Poll(cpu: 0, gpu: 0, ok: false));

        // A failed WMI read surfaces as 0, which is not a temperature. The banner says the read
        // failed in its own words; the display holds the last thing it actually knew.
        Assert.Equal(70, vm.DisplayCpuTemp);
        Assert.Equal(60, vm.DisplayGpuTemp);
        // The snapshot keeps the failure, so nothing downstream is fooled into thinking it read.
        Assert.Equal(0, vm.Sensors.CpuTemp);
        Assert.False(vm.Sensors.Ok);
    }

    [Fact]
    public async Task The_tray_tooltip_reads_the_smoothed_pair_too()
    {
        var vm = Make();
        for (var i = 0; i < TemperatureAverage.DisplaySamples; i++)
            await vm.OnSensorPollAsync(Poll(cpu: 60, gpu: 50));

        await vm.OnSensorPollAsync(Poll(cpu: 100, gpu: 50));

        // It is the same number in a smaller place, and a tooltip that disagreed with the window
        // it belongs to would just look broken.
        Assert.Contains($"CPU {vm.DisplayCpuTemp}°", vm.TrayTooltip);
        Assert.DoesNotContain("CPU 100°", vm.TrayTooltip);
    }
}
