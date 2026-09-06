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

    private (MainViewModel vm, FakeGigabyteWmi wmi) Make(ModelProfile? profile = null)
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
            Settings = new AppSettings(),
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(new FakeKeyboardHid(), KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = false,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        };
        return (new MainViewModel(services), wmi);
    }

    private static SensorSnapshot Poll(int cpu, int duty, bool ok = true) =>
        new(cpu, 50, 3000, 3000, duty, duty, ok, ok ? null : "getCpuTemp: failed (fake)");

    [Fact]
    public async Task A_cpu_at_the_trigger_with_slow_fans_is_pinned_to_full_and_the_owner_is_told()
    {
        var (vm, wmi) = Make();

        await vm.OnSensorPollAsync(Poll(cpu: 92, duty: 10));

        Assert.Contains(wmi.Calls, c => c.Method == "SetFixedFanSpeed" && c.Data == 229);
        Assert.Contains(wmi.Calls, c => c.Method == "SetFixedFanStatus" && c.Data == 1);
        Assert.Equal(BannerKind.Warning, vm.Banner);
        Assert.Contains("Fans forced to full", vm.BannerText);
        Assert.Contains("92", vm.BannerText);
        // The window has to agree with the machine: it really is in Turbo now.
        Assert.Equal(FanMode.Turbo, vm.SelectedMode);
    }

    [Fact]
    public async Task It_does_not_re_apply_turbo_on_every_poll_while_the_machine_stays_hot()
    {
        var (vm, wmi) = Make();
        await vm.OnSensorPollAsync(Poll(cpu: 92, duty: 10));
        wmi.Calls.Clear();

        // The duty read-back lags the write by a second or two, so these look just like the
        // poll that fired.
        await vm.OnSensorPollAsync(Poll(cpu: 95, duty: 10));
        await vm.OnSensorPollAsync(Poll(cpu: 97, duty: 10));

        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public async Task The_notice_survives_the_polls_that_follow_it()
    {
        var (vm, _) = Make();
        await vm.OnSensorPollAsync(Poll(cpu: 92, duty: 10));

        await vm.OnSensorPollAsync(Poll(cpu: 70, duty: 100));
        await vm.OnSensorPollAsync(Poll(cpu: 55, duty: 40));

        // Nothing was reverted, so nothing may quietly stop saying so either.
        Assert.Contains("Fans forced to full", vm.BannerText);
    }

    [Fact]
    public async Task A_failed_sensor_read_showing_as_zero_degrees_does_nothing()
    {
        var (vm, wmi) = Make();

        await vm.OnSensorPollAsync(Poll(cpu: 0, duty: 0, ok: false));

        Assert.Empty(wmi.Calls);
        Assert.DoesNotContain("forced to full", vm.BannerText);
    }

    [Fact]
    public async Task A_cool_machine_is_left_alone()
    {
        var (vm, wmi) = Make();

        await vm.OnSensorPollAsync(Poll(cpu: 62, duty: 30));

        Assert.Empty(wmi.Calls);
        Assert.Equal(BannerKind.None, vm.Banner);
    }

    [Fact]
    public async Task A_machine_already_cooling_itself_is_left_alone()
    {
        var (vm, wmi) = Make();

        await vm.OnSensorPollAsync(Poll(cpu: 95, duty: 100));

        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public async Task On_a_model_it_cannot_write_to_it_neither_acts_nor_complains()
    {
        // The WMI writes are withheld here, so there is nothing the watchdog could do about the
        // temperature - and a warning once a second on top of the banner that already explains
        // the model would be noise, not help.
        var (vm, wmi) = Make(ModelProfile.Detect("Some Other Laptop"));

        await vm.OnSensorPollAsync(Poll(cpu: 99, duty: 0));
        await vm.OnSensorPollAsync(Poll(cpu: 99, duty: 0));

        Assert.Empty(wmi.Calls);
        Assert.DoesNotContain("forced to full", vm.BannerText);
        Assert.Equal(BannerKind.Error, vm.Banner);   // still the read-only model banner
    }

    [Fact]
    public async Task A_failed_turbo_write_is_reported_rather_than_swallowed()
    {
        var (vm, wmi) = Make();
        wmi.FailOn.Add("SetFixedFanSpeed");

        await vm.OnSensorPollAsync(Poll(cpu: 92, duty: 10));

        Assert.Equal(BannerKind.Error, vm.Banner);
        Assert.Contains("SetFixedFanSpeed", vm.BannerText);
    }
}
