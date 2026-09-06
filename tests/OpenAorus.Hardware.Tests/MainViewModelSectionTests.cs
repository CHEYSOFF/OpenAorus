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
/// The window gained a Cooling | Lighting switch, and the Lighting half only exists on a machine
/// with a keyboard the app can drive. The rule that matters is that nothing can strand the owner
/// on a section that is not there.
/// </summary>
public class MainViewModelSectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private MainViewModel Build(bool keyboardPresent)
    {
        var hid = new FakeKeyboardHid { IsPresent = keyboardPresent };
        var wmi = new FakeGigabyteWmi();
        var profile = ModelProfile.Detect("AORUS 17G KD");
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json");

        return new MainViewModel(new AppServices
        {
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile, delay: _ => Task.CompletedTask),
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = new SettingsStore(path),
            Settings = new AppSettings(),
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = keyboardPresent,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        });
    }

    [Fact]
    public void The_window_opens_on_cooling()
    {
        Assert.Equal(AppSection.Cooling, Build(keyboardPresent: true).SelectedSection);
    }

    [Fact]
    public void Lighting_can_be_selected_when_there_is_a_keyboard()
    {
        var vm = Build(keyboardPresent: true);

        Assert.True(vm.LightingAvailable);
        vm.SelectedSection = AppSection.Lighting;

        Assert.Equal(AppSection.Lighting, vm.SelectedSection);
    }

    /// <summary>
    /// With no keyboard the switch is not drawn at all, so this can only be reached by a stale
    /// binding or a later caller - and either way the panel it would show is empty.
    /// </summary>
    [Fact]
    public void Lighting_cannot_be_selected_when_there_is_no_keyboard()
    {
        var vm = Build(keyboardPresent: false);

        Assert.False(vm.LightingAvailable);
        vm.SelectedSection = AppSection.Lighting;

        Assert.Equal(AppSection.Cooling, vm.SelectedSection);
    }

    [Fact]
    public void The_section_command_switches_sections()
    {
        var vm = Build(keyboardPresent: true);

        vm.SelectSectionCommand.Execute(AppSection.Lighting);
        Assert.Equal(AppSection.Lighting, vm.SelectedSection);

        vm.SelectSectionCommand.Execute(AppSection.Cooling);
        Assert.Equal(AppSection.Cooling, vm.SelectedSection);
    }

    [Fact]
    public void The_lighting_view_model_owns_the_per_key_editor()
    {
        var vm = Build(keyboardPresent: true);

        Assert.NotNull(vm.Lighting.PerKey);
        Assert.Equal(
            KeyLayout.For(KeyboardLayout.EngUk).RealKeys.Count(),
            vm.Lighting.PerKey.Keys.Count);
    }
}
