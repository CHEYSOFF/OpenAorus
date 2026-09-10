using OpenAorus.Hardware.Wmi;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Battery;

public sealed record BatteryStatus(bool CustomLimitEnabled, int StopPercent, int CycleCount, int Health, bool Ok, string? Error);

/// <summary>Charge policy 0 = standard, 4 = custom stop (values GCC writes). Stop is 60..100 %.</summary>
public sealed class BatteryController
{
    public const byte PolicyStandard = 0;
    public const byte PolicyCustom = 4;
    public const int MinStop = 60;
    public const int MaxStop = 100;

    private readonly IGigabyteWmi _wmi;
    private readonly Func<bool> _writesUnlocked;

    /// <param name="wmi">The only door to the embedded controller.</param>
    /// <param name="writesUnlocked">Whether the WMI schema on this machine has been proved, read at
    /// every write rather than captured. Null leaves the gate open, so every construction site that
    /// predates the schema feature is unchanged.</param>
    public BatteryController(IGigabyteWmi wmi, Func<bool>? writesUnlocked = null)
    {
        _wmi = wmi;
        _writesUnlocked = writesUnlocked ?? (() => true);
    }

    /// <summary>Writes the charge policy and the stop percentage.</summary>
    /// <param name="enabled">Whether the custom stop is in force.</param>
    /// <param name="stopPercent">Where to stop charging, clamped to
    /// <see cref="MinStop"/>..<see cref="MaxStop"/>.</param>
    /// <returns>What the controller said, or a refusal if writes are locked.</returns>
    public WmiResult SetLimit(bool enabled, int stopPercent)
    {
        // Here rather than at the battery card, for the reason the fan gate is in FanController:
        // ApplySavedAsync writes the saved charge limit at every startup and at every resume, and
        // --apply does it with no window in the process at all.
        if (!_writesUnlocked())
            return WmiResult.Fail(SchemaState.LockedRefusal("the battery charge limit"));

        var policy = enabled ? PolicyCustom : PolicyStandard;
        var stop = enabled ? (byte)Math.Clamp(stopPercent, MinStop, MaxStop) : (byte)MaxStop;

        var r1 = _wmi.SetData("SetChargePolicy", policy);
        if (!r1.Success) return WmiResult.Fail($"SetChargePolicy failed: {r1.Error}");
        var r2 = _wmi.SetData("SetChargeStop", stop);
        if (!r2.Success) return WmiResult.Fail($"SetChargeStop failed: {r2.Error}");
        return WmiResult.Ok();
    }

    public BatteryStatus Read()
    {
        var errors = new List<string>();
        int Value(string method)
        {
            var r = _wmi.Get(method);
            if (!r.Success) { errors.Add($"{method}: {r.Error}"); return 0; }
            return r.GetInt("Data");
        }

        var policy = Value("GetChargePolicy");
        var stop = Value("GetChargeStop");
        var cycles = Value("GetBatteryCount");
        var health = Value("GetBatteryHealth");

        return new BatteryStatus(
            CustomLimitEnabled: policy != PolicyStandard,
            StopPercent: stop == 0 ? MaxStop : Math.Clamp(stop, MinStop, MaxStop),
            CycleCount: cycles,
            Health: health,
            Ok: errors.Count == 0,
            Error: errors.Count == 0 ? null : string.Join("; ", errors));
    }
}
