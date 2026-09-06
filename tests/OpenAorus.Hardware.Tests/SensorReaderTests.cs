using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;

namespace OpenAorus.Hardware.Tests;

public class SensorReaderTests
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");

    [Fact]
    public void SwapBytes_swaps_low_and_high_byte()
    {
        Assert.Equal(0x1234, SensorReader.SwapBytes(0x3412));
        Assert.Equal(0, SensorReader.SwapBytes(0));
    }

    [Fact]
    public void Read_collects_temps_rpm_and_duty_percent()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("getCpuTemp", (ushort)61);
        wmi.Respond("getGpuTemp1", (ushort)55);
        wmi.Respond("getRpm1", (ushort)0x6009);   // swapped -> 0x0960 = 2400
        wmi.Respond("getRpm2", (ushort)0xFC08);   // swapped -> 0x08FC = 2300
        wmi.Respond("GetCPUFanDuty", (byte)115);
        wmi.Respond("GetGPUFanDuty", (byte)229);
        var s = new SensorReader(wmi, Kd).Read();
        Assert.True(s.Ok);
        Assert.Equal(61, s.CpuTemp);
        Assert.Equal(55, s.GpuTemp);
        Assert.Equal(2400, s.Fan1Rpm);
        Assert.Equal(2300, s.Fan2Rpm);
        Assert.Equal(50, s.Fan1DutyPercent);
        Assert.Equal(100, s.Fan2DutyPercent);
    }

    [Fact]
    public void Read_does_not_swap_rpm_when_profile_says_so()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("getRpm1", (ushort)2400);
        var p = Kd with { RpmByteSwapped = false };
        Assert.Equal(2400, new SensorReader(wmi, p).Read().Fan1Rpm);
    }

    [Fact]
    public void Gpu_temp_falls_back_to_thermal_data_when_zero()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("getGpuTemp1", (ushort)0);
        wmi.Responses["GetThermalData"] = new Dictionary<string, object>
        {
            ["Thermal1"] = (byte)48, ["Thermal2"] = (byte)57, ["Thermal3"] = (byte)40,
        };
        Assert.Equal(57, new SensorReader(wmi, Kd).Read().GpuTemp);
    }

    [Fact]
    public void Cpu_failure_marks_snapshot_not_ok_but_keeps_other_values()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.FailOn.Add("getCpuTemp");
        wmi.Respond("getRpm1", (ushort)0x6009);
        var s = new SensorReader(wmi, Kd).Read();
        Assert.False(s.Ok);
        Assert.Contains("getCpuTemp", s.Error);
        Assert.Equal(2400, s.Fan1Rpm);
    }
}
