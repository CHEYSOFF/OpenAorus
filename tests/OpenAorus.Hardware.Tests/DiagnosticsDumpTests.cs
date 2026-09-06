using OpenAorus.Hardware.Diagnostics;
using OpenAorus.Hardware.Profiles;

namespace OpenAorus.Hardware.Tests;

public class DiagnosticsDumpTests
{
    [Fact]
    public void Render_lists_model_profile_and_every_get_method()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("getCpuTemp", (ushort)61);
        wmi.FailOn.Add("GetVRStatus");
        var text = DiagnosticsDump.Render(wmi, ModelProfile.Detect("AORUS 17G KD"), "0.1.0-test");

        Assert.Contains("OpenAorus 0.1.0-test", text);
        Assert.Contains("Model: AORUS 17G KD (Tested, DutyMax=229, Fans=2)", text);
        Assert.Contains("getCpuTemp: Data=61", text);
        Assert.Contains("GetVRStatus: ERROR", text);
        Assert.All(DiagnosticsDump.GetMethods, m => Assert.Contains(m + ":", text));
        Assert.Contains("Fan table", text);
        Assert.Equal(DiagnosticsDump.GetMethods.Length + 15, wmi.Calls.Count); // + 15 GetFanIndexValue reads
    }

    [Fact]
    public void GetMethods_includes_fan_thermal_and_battery_reads_and_no_setters()
    {
        Assert.Contains("GetDeepFan", DiagnosticsDump.GetMethods);
        Assert.Contains("GetThermalData", DiagnosticsDump.GetMethods);
        Assert.Contains("GetChargeStop", DiagnosticsDump.GetMethods);
        Assert.DoesNotContain(DiagnosticsDump.GetMethods, m => m.StartsWith("Set"));
        Assert.DoesNotContain("GetFanIndexValue", DiagnosticsDump.GetMethods); // needs an Index arg
    }
}
