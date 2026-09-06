using System.IO;
using System.Reflection;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Diagnostics;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;
using OpenAorus.Hardware.Platform;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.App;

public sealed class AppServices
{
    public required ModelProfile Profile { get; init; }
    public required IGigabyteWmi Wmi { get; init; }
    public required FanController Fans { get; init; }
    public required SensorReader Sensors { get; init; }
    public required BatteryController Battery { get; init; }
    public required SettingsStore Store { get; init; }
    public required AppSettings Settings { get; init; }
    public required IGccSystem Gcc { get; init; }
    public required LightingController Lighting { get; init; }

    /// <summary>Whether a supported lighting collection was found. False hides the lighting UI
    /// and makes every lighting call a no-op; nothing else about the app changes.</summary>
    public required bool KeyboardPresent { get; init; }
    public required string Version { get; init; }
    public required string ExePath { get; init; }

    public static AppServices Create()
    {
        var profile = ModelProfile.Detect(SystemInfo.GetProductName());
        var wmi = new GigabyteWmi();
        var store = new SettingsStore(SettingsStore.DefaultPath);
        // Loaded once and shared: a second store.Load() here would hand the app a divergent copy
        // of the settings, and every save from one would quietly undo the other.
        var settings = store.Load();
        var keyboard = KeyboardHid.Open();
        var layout = settings.Lighting.LayoutOverride is { } forced
            ? KeyLayout.For(forced)
            : KeyLayout.ForProduct(keyboard.Identity?.Pid ?? 0);

        return new AppServices
        {
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile),
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = store,
            Settings = settings,
            Gcc = new WindowsGccSystem(),
            Lighting = new LightingController(keyboard, layout),
            KeyboardPresent = keyboard.IsPresent,
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
            ExePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location,
        };
    }

    /// <summary>Re-applies the persisted fan mode, charge limit and lighting (startup, resume, --apply).
    /// Attempts all three even if one fails, so a fan-write failure never strands the saved charge limit
    /// or the saved lighting unrestored. A machine with no keyboard simply skips the lighting step.</summary>
    public async Task<WmiResult> ApplySavedAsync()
    {
        if (!Profile.CanWrite) return WmiResult.Fail("read-only model");

        var fans = await Fans.ApplyAsync(Settings.Mode, Settings.FixedPercent, Settings.ToCurve());
        var battery = Battery.SetLimit(Settings.ChargeLimitEnabled, Settings.ChargeStopPercent);

        var errors = new List<string>();
        if (!fans.Success) errors.Add($"fan mode: {fans.Error}");
        if (!battery.Success) errors.Add($"charge limit: {battery.Error}");

        if (KeyboardPresent)
        {
            var saved = Settings.Lighting;
            // Custom is the only effect whose colours live outside the effect report, and it is
            // only restorable if a full slot's worth of them was saved. Anything else - including
            // a colour list SettingsStore had to discard - falls back to selecting the effect,
            // which leaves the keyboard showing the colours it already holds.
            var lighting = saved.Effect == LightEffect.Custom && saved.PerKeyColors.Count == KeyLayout.SlotCount
                ? await Lighting.ApplyPerKeyAsync(saved.PerKeyColors, saved.BrightnessPercent)
                : await Lighting.ApplyEffectAsync(saved.ToParameters());
            if (!lighting.Success) errors.Add($"lighting: {lighting.Error}");
        }

        return errors.Count == 0 ? WmiResult.Ok() : WmiResult.Fail(string.Join("; ", errors));
    }

    public string WriteDiagnostics()
    {
        var text = DiagnosticsDump.Render(Wmi, Profile, Version);
        var dir = Path.GetDirectoryName(Store.Path)!;
        Directory.CreateDirectory(dir);
        var safe = string.Concat(Profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Replace(' ', '-');
        var path = Path.Combine(dir, $"diagnostics-{safe}.txt");
        File.WriteAllText(path, text);
        return path;
    }
}
