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
/// What the owner is told when <see cref="SettingsStore.Load"/> had to change their file.
/// </summary>
/// <remarks>
/// A fan repair changes how the machine cools itself, so the notice has to name the fans rather
/// than reuse the lighting sentence that was there before - an owner who reads "some saved
/// lighting settings were out of range" and then finds their custom curve gone has been misled
/// by the app about the one thing it changed for their own safety.
/// </remarks>
public class MainViewModelRepairNoticeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private MainViewModel FromSettingsFile(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        System.IO.File.WriteAllText(path, json);

        var profile = ModelProfile.Detect("AORUS 17G KD");
        var wmi = new FakeGigabyteWmi();
        var store = new SettingsStore(path);
        return new MainViewModel(new AppServices
        {
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile, delay: _ => Task.CompletedTask),
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = store,
            Settings = store.Load(),
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(new FakeKeyboardHid(), KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = false,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        });
    }

    [Fact]
    public void A_repaired_fan_setting_is_named_in_the_notice()
    {
        var vm = FromSettingsFile("{ \"Mode\": \"Fixed\", \"FixedPercent\": 0 }");

        Assert.Equal(BannerKind.Warning, vm.Banner);
        Assert.Contains("fan", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(FanSafety.MinFixedPercent.ToString(), vm.BannerText);
        Assert.DoesNotContain("lighting", vm.BannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_repaired_lighting_setting_still_reads_as_it_did()
    {
        var vm = FromSettingsFile("{ \"Lighting\": { \"Effect\": 99 } }");

        Assert.Equal(BannerKind.Warning, vm.Banner);
        Assert.Contains("lighting", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("curve", vm.BannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_repaired_on_both_halves_says_both()
    {
        var vm = FromSettingsFile(
            "{ \"FixedPercent\": 0, \"Curve\": [ { \"Temperature\": 30, \"DutyPercent\": 0 }, " +
            "{ \"Temperature\": 90, \"DutyPercent\": 0 } ], \"Lighting\": { \"Effect\": 99 } }");

        Assert.Contains("lighting", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("curve", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Everything else in your settings was kept.", vm.BannerText);
    }

    [Fact]
    public void A_repaired_hotkey_setting_is_named_in_the_notice()
    {
        var vm = FromSettingsFile("{ \"Hotkeys\": { \"OverlaySeconds\": 0 } }");

        Assert.Equal(BannerKind.Warning, vm.Banner);
        Assert.Contains("hotkey", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(HotkeySettings.MaxOverlaySeconds.ToString(), vm.BannerText);
        // Naming the wrong half is the mistake this notice was split up to avoid.
        Assert.DoesNotContain("lighting", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("curve", vm.BannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_fan_repair_does_not_start_talking_about_hotkeys()
    {
        var vm = FromSettingsFile("{ \"Mode\": \"Fixed\", \"FixedPercent\": 0 }");

        Assert.DoesNotContain("hotkey", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overlay", vm.BannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_settled_file_with_hotkeys_in_it_raises_no_notice()
    {
        var vm = FromSettingsFile(
            "{ \"Hotkeys\": { \"Enabled\": true, \"OverlayForFanMode\": true, \"OverlaySeconds\": 3 } }");

        Assert.Equal(BannerKind.None, vm.Banner);
        Assert.Equal("", vm.BannerText);
    }

    [Fact]
    public void A_repaired_gate_record_says_that_writes_are_locked_rather_than_blaming_the_fans()
    {
        var vm = FromSettingsFile("{ \"Schema\": { \"GatesPassed\": true } }");

        Assert.Equal(BannerKind.Warning, vm.Banner);
        Assert.Contains("hardware check", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lighting", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("curve", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hotkey", vm.BannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_settled_file_raises_no_notice_at_all()
    {
        var vm = FromSettingsFile("{ \"Mode\": \"Gaming\", \"FixedPercent\": 45 }");

        Assert.Equal(BannerKind.None, vm.Banner);
        Assert.Equal("", vm.BannerText);
    }
}
