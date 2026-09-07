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
using OpenAorus.Hardware.Ui;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// What the owner is told when the Fn row is not being listened for at all.
/// </summary>
/// <remarks>
/// <para>
/// Both channels write down a start failure - <c>IsListening</c> and <c>StartError</c> - and
/// until now nothing above them read either, so a machine where neither channel could be opened
/// disabled the feature in silence and left the evidence in a diagnostics dump the owner has no
/// reason to export. These pin the other half of that: it is said, and said once.
/// </para>
/// <para>
/// One channel down is deliberately not a notice. They carry different keys, they fail for
/// unrelated reasons, and a chassis with no Gigabyte WMI provider is the common case rather than
/// a fault - warning about it on every launch would train the owner to ignore the banner.
/// </para>
/// <para>
/// The model is an unrecognised one wherever the notice itself is being read, and not only
/// because that is where a dead channel is likeliest: an unknown model's banner is permanent, so
/// the startup poll the view model fires on its own thread cannot clear the notice out from under
/// the assertion. The one test that needs the notice to be cleared uses a recognised model for
/// exactly the opposite reason.
/// </para>
/// </remarks>
public class MainViewModelHotkeyChannelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private MainViewModel Build(FakeHotkeySource raw, FakeWmiEventSource wmiEvents, ModelProfile? model = null)
    {
        var profile = model ?? ModelProfile.Detect("Some Other Laptop");
        var wmi = new FakeGigabyteWmi();
        var settings = new AppSettings();
        return new MainViewModel(new AppServices
        {
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile, delay: _ => Task.CompletedTask),
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = new SettingsStore(Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json")),
            Settings = settings,
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(new FakeKeyboardHid(), KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = false,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
            Hotkeys = new HotkeyService(raw, wmiEvents, settings.Hotkeys, () => settings.Mode, () => 0),
        });
    }

    [Fact]
    public async Task Neither_channel_opening_is_said_in_the_channels_own_words()
    {
        var raw = new FakeHotkeySource { FailToStartWith = "Win32 error 5" };
        var events = new FakeWmiEventSource { FailToStartWith = "ManagementException: Access denied" };
        var vm = Build(raw, events);

        await vm.InitializeAsync();
        vm.Shutdown();

        Assert.Equal(BannerKind.Warning, vm.Banner);
        // What it costs the owner, and why - both reasons, because either alone names half a
        // failure. One of the two is often the interesting one and there is no telling which.
        Assert.Contains("Fn", vm.BannerText, StringComparison.Ordinal);
        Assert.Contains("Win32 error 5", vm.BannerText, StringComparison.Ordinal);
        Assert.Contains("Access denied", vm.BannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_is_said_once_and_not_once_a_poll()
    {
        // A recognised model, because an override notice is a one-shot there: the next report of
        // any kind reveals whatever was underneath it, which is what BannerState documents and
        // what the settings-repair notice already lives with. That is what makes "once" visible -
        // each re-raise after a clearing is a fresh transition into the notice, and a counter over
        // them sees a second one however identical its text is.
        var raw = new FakeHotkeySource { FailToStartWith = "Win32 error 5" };
        var events = new FakeWmiEventSource { FailToStartWith = "ManagementException: Access denied" };
        var vm = Build(raw, events, ModelProfile.Detect("AORUS 17G KD"));

        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.BannerText) &&
                vm.BannerText.Contains("Fn", StringComparison.Ordinal))
                raised++;
        };

        await vm.InitializeAsync();
        await vm.OnSensorPollAsync(new SensorSnapshot(60, 50, 3000, 3000, 30, 30, true, null));

        // Read once and then out of the way, like every other override notice in this app.
        Assert.DoesNotContain("Fn", vm.BannerText, StringComparison.Ordinal);

        await vm.OnSensorPollAsync(new SensorSnapshot(61, 50, 3000, 3000, 30, 30, true, null));
        await vm.OnSensorPollAsync(new SensorSnapshot(62, 50, 3000, 3000, 30, 30, true, null));
        vm.Shutdown();

        // The channels are still shut and StartError still says so, so anything that re-read it
        // per poll - or per report, on a channel that is faulting rather than silent - would put
        // the notice straight back over whatever the owner was reading.
        Assert.Equal(1, raised);
        Assert.DoesNotContain("Fn", vm.BannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_channel_still_open_is_not_worth_a_banner()
    {
        // The raw-input channel carries the fan and backlight row; the WMI subscription carries
        // only the touchpad and radio notices, and wants an elevated process and a Gigabyte
        // provider. Half the Fn row still working is not the failure the spec asks to announce.
        var raw = new FakeHotkeySource();
        var events = new FakeWmiEventSource { FailToStartWith = "ManagementException: provider not found" };
        var vm = Build(raw, events);

        await vm.InitializeAsync();
        vm.Shutdown();

        Assert.DoesNotContain("Fn", vm.BannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_channels_opening_says_nothing_at_all()
    {
        var raw = new FakeHotkeySource();
        var events = new FakeWmiEventSource();
        var vm = Build(raw, events);

        await vm.InitializeAsync();
        vm.Shutdown();

        // An unrecognised model's own banner, and nothing about hotkeys layered over it.
        Assert.Equal(BannerKind.Error, vm.Banner);
        Assert.DoesNotContain("Fn", vm.BannerText, StringComparison.Ordinal);
    }
}
