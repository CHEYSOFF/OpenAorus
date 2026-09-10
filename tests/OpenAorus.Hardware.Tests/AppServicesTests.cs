using System.IO;
using OpenAorus.App;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

public class AppServicesTests
{
    private static readonly RgbColor SavedColor = new(0x11, 0x22, 0x33);
    private const int SavedBrightness = 42;

    /// <summary>An AppServices wired to fakes. Nothing here touches WMI, HID or the settings file.</summary>
    private static AppServices Build(ModelProfile profile, FakeGigabyteWmi wmi, FakeKeyboardHid keyboard)
    {
        var settings = new AppSettings();
        settings.Lighting.Effect = LightEffect.Static;
        settings.Lighting.Color = SavedColor;
        settings.Lighting.BrightnessPercent = SavedBrightness;

        return new AppServices
        {
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile, delay: _ => Task.CompletedTask),
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = new SettingsStore(Path.Combine(Path.GetTempPath(), "OpenAorus.Tests", "settings.json")),
            Settings = settings,
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(keyboard, KeyLayout.For(KeyboardLayout.EngUk), delay: _ => Task.CompletedTask),
            KeyboardPresent = keyboard.IsPresent,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        };
    }

    [Fact]
    public void The_exported_dump_says_what_is_registered_and_what_the_gates_made_of_it()
    {
        // The machine most likely to press Export diagnostics, end to end: nothing registered, so
        // all 72 readings below fail, and until this the file carried no reason for any of it.
        var dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var profile = ModelProfile.Detect("AORUS 17G KD");
            var wmi = new FakeGigabyteWmi();
            var record = new SchemaRecord { GateSummary = "Gate A: 3 of 72 readings differed" };
            var services = new AppServices
            {
                Profile = profile,
                Wmi = wmi,
                Fans = new FanController(wmi, profile, delay: _ => Task.CompletedTask),
                Sensors = new SensorReader(wmi, profile),
                Battery = new BatteryController(wmi),
                Store = new SettingsStore(Path.Combine(dir, "settings.json")),
                Settings = new AppSettings(),
                Schema = new SchemaService(new FakeSchemaSystem(), record, SchemaMof.Fingerprint),
                Gcc = new FakeGccSystem(),
                Lighting = new LightingController(
                    new FakeKeyboardHid(), KeyLayout.For(KeyboardLayout.EngUk), delay: _ => Task.CompletedTask),
                KeyboardPresent = false,
                Version = "0.0.0",
                ExePath = "OpenAorus.Tests.exe",
            };

            var text = System.IO.File.ReadAllText(services.WriteDiagnostics());

            Assert.Contains("WMI schema: Absent - writes locked", text);
            Assert.Contains("live fingerprint: none", text);
            Assert.Contains("Gate A: 3 of 72 readings differed", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>The 0x02 "set effect" report out of a sequence that also carries a 0x82 status read.</summary>
    private static byte[] EffectReport(FakeKeyboardHid keyboard) =>
        Assert.Single(keyboard.Written, r => r[1] == 0x02);

    [Fact]
    public async Task An_unrecognised_model_still_gets_its_saved_lighting_back()
    {
        // Lighting is HID: no elevation, no per-model duty scale, nothing that an unknown model
        // makes unsafe. Unrecognised models are also the common case, so this is most owners.
        var profile = ModelProfile.Detect("Some Other Laptop");
        Assert.False(profile.CanWrite);
        var keyboard = new FakeKeyboardHid();

        var result = await Build(profile, new FakeGigabyteWmi(), keyboard).ApplySavedAsync();

        var report = EffectReport(keyboard);
        Assert.Equal((byte)LightEffect.Static, report[10]);
        Assert.Equal(SavedBrightness, report[12]);
        Assert.Equal(new byte[] { SavedColor.R, SavedColor.G, SavedColor.B }, report[14..17]);

        // ...and the caller is still told the WMI half was withheld, in the same words as before.
        Assert.False(result.Success);
        Assert.Contains("read-only model", result.Error!);
    }

    [Fact]
    public async Task An_unrecognised_model_writes_nothing_over_wmi()
    {
        // The other half of the guard: the fan and charge-limit writes stay withheld, which is
        // the whole reason the read-only rule exists.
        var wmi = new FakeGigabyteWmi();

        await Build(ModelProfile.Detect("Some Other Laptop"), wmi, new FakeKeyboardHid()).ApplySavedAsync();

        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public async Task A_recognised_model_applies_fans_charge_limit_and_lighting()
    {
        var profile = ModelProfile.Detect("AORUS 17G KD");
        Assert.True(profile.CanWrite);
        var wmi = new FakeGigabyteWmi();
        var keyboard = new FakeKeyboardHid();

        var result = await Build(profile, wmi, keyboard).ApplySavedAsync();

        Assert.True(result.Success, result.Error);
        Assert.Contains("SetChargePolicy", wmi.MethodsCalled);
        Assert.Equal((byte)LightEffect.Static, EffectReport(keyboard)[10]);
    }

    [Fact]
    public async Task A_machine_with_no_keyboard_skips_the_lighting_step()
    {
        var keyboard = new FakeKeyboardHid { IsPresent = false };

        var result = await Build(ModelProfile.Detect("AORUS 17G KD"), new FakeGigabyteWmi(), keyboard).ApplySavedAsync();

        Assert.True(result.Success, result.Error);
        Assert.Empty(keyboard.Written);
    }
}
