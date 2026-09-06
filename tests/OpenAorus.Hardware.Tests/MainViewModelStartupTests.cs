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
/// Restoring the saved lighting is not gated on the model being one the profile table knows.
/// Only the two WMI writes are withheld there - the duty scale is a per-model guess - and
/// lighting is HID, with nothing model-specific to get wrong. Unrecognised models are the
/// common case, so gating it would leave most owners with no lighting restore at all, and the
/// "read-only model" that always comes back with it is the state the model banner already
/// explains rather than a startup failure to put on the status line.
/// </summary>
/// <remarks>
/// <see cref="AppServicesTests"/> covers the same rule one layer down. This one is here because
/// the view model repeats the decision: it chooses whether to report what came back, and both
/// halves of that choice can be re-gated with every other test still green.
/// The resume path makes the same choice and is deliberately left unpinned - it is a
/// <c>private async void</c> handler behind a hard-coded three-second delay, and a seam cut
/// through the view model to reach it would cost more than the duplicate it covers.
/// </remarks>
public class MainViewModelStartupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private AppServices Services(ModelProfile profile, FakeKeyboardHid hid)
    {
        var wmi = new FakeGigabyteWmi();
        return new AppServices
        {
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile, delay: _ => Task.CompletedTask),
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = new SettingsStore(Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json")),
            Settings = new AppSettings(),
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = true,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        };
    }

    [Fact]
    public async Task Startup_on_a_read_only_model_restores_lighting_and_stays_quiet()
    {
        var keyboard = new FakeKeyboardHid();
        var vm = new MainViewModel(Services(ModelProfile.Detect("Some Other Laptop"), keyboard));

        await vm.InitializeAsync();
        vm.Shutdown();

        Assert.False(vm.CanWrite);
        Assert.Contains(keyboard.Written, r => r[1] == 0x02);
        Assert.Equal("", vm.StatusLine);   // no startup-failure status on a read-only model
    }
}
