using OpenAorus.Hardware.Wmi;

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

    public BatteryController(IGigabyteWmi wmi) => _wmi = wmi;

    public WmiResult SetLimit(bool enabled, int stopPercent)
    {
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
