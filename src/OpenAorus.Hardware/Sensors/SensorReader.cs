using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Sensors;

public sealed record SensorSnapshot(
    int CpuTemp, int GpuTemp, int Fan1Rpm, int Fan2Rpm,
    int Fan1DutyPercent, int Fan2DutyPercent, bool Ok, string? Error)
{
    public static SensorSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, false, "not read yet");
}

/// <summary>Reads temps, RPM and duty through GB_WMIACPI_Get. Cheap enough to call every second.</summary>
public sealed class SensorReader
{
    private readonly IGigabyteWmi _wmi;
    private readonly ModelProfile _profile;

    public SensorReader(IGigabyteWmi wmi, ModelProfile profile)
    {
        _wmi = wmi;
        _profile = profile;
    }

    /// <summary>GCC's GetBytesInt16 for Gigabyte models: the EC reports RPM big-endian.</summary>
    public static int SwapBytes(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    public SensorSnapshot Read()
    {
        var errors = new List<string>();

        int Value(string method, string param = "Data")
        {
            var r = _wmi.Get(method);
            if (!r.Success) { errors.Add($"{method}: {r.Error}"); return 0; }
            return r.GetInt(param);
        }

        var cpu = Value("getCpuTemp");
        var gpu = _profile.HasGpuTemp1 ? Value("getGpuTemp1") : 0;
        if (gpu <= 0)
        {
            var thermal = _wmi.Get("GetThermalData");
            if (thermal.Success) gpu = thermal.GetInt("Thermal2");
        }

        var rpm1 = Value("getRpm1");
        var rpm2 = _profile.FanCount >= 2 ? Value("getRpm2") : 0;
        if (_profile.RpmByteSwapped) { rpm1 = SwapBytes(rpm1); rpm2 = SwapBytes(rpm2); }

        var duty1 = _profile.ToPercent(Value("GetCPUFanDuty"));
        var duty2 = _profile.FanCount >= 2 ? _profile.ToPercent(Value("GetGPUFanDuty")) : 0;

        return new SensorSnapshot(cpu, gpu, rpm1, rpm2, duty1, duty2,
            Ok: errors.Count == 0,
            Error: errors.Count == 0 ? null : string.Join("; ", errors));
    }
}
