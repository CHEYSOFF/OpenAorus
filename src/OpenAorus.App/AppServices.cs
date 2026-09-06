using System.IO;
using System.Reflection;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Diagnostics;
using OpenAorus.Hardware.Fans;
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
    public required string Version { get; init; }
    public required string ExePath { get; init; }

    public static AppServices Create()
    {
        var profile = ModelProfile.Detect(SystemInfo.GetProductName());
        var wmi = new GigabyteWmi();
        var store = new SettingsStore(SettingsStore.DefaultPath);
        return new AppServices
        {
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile),
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = store,
            Settings = store.Load(),
            Gcc = new WindowsGccSystem(),
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
            ExePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location,
        };
    }

    /// <summary>Re-applies the persisted fan mode and charge limit (startup, resume, --apply). Attempts both even
    /// if one fails, so a fan-write failure never strands the saved charge limit unrestored.</summary>
    public async Task<WmiResult> ApplySavedAsync()
    {
        if (!Profile.CanWrite) return WmiResult.Fail("read-only model");

        var fans = await Fans.ApplyAsync(Settings.Mode, Settings.FixedPercent, Settings.ToCurve());
        var battery = Battery.SetLimit(Settings.ChargeLimitEnabled, Settings.ChargeStopPercent);
        if (fans.Success && battery.Success) return WmiResult.Ok();

        var errors = new List<string>();
        if (!fans.Success) errors.Add($"fan mode: {fans.Error}");
        if (!battery.Success) errors.Add($"charge limit: {battery.Error}");
        return WmiResult.Fail(string.Join("; ", errors));
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
