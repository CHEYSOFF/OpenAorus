using OpenAorus.Hardware.Diagnostics;
using OpenAorus.Hardware.Hotkeys;
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

    [Fact]
    public void Render_carries_what_the_hotkey_channels_saw()
    {
        // The bug report is where a rejected report has to surface. Without this, a report that
        // arrives and decodes to nothing is indistinguishable from a chassis that sent none -
        // and those two have opposite fixes.
        var trace = new HotkeyTrace();
        trace.RecordReport(new byte[] { 4, 1, 12, 38 });
        trace.RecordUnreadablePacket(36);

        var text = DiagnosticsDump.Render(new FakeGigabyteWmi(), ModelProfile.Detect("AORUS 17G KD"), "0.1.0-test", trace);

        Assert.Contains("Hotkey channels: reports=1", text);
        Assert.Contains("04 01 0C 26", text);
        Assert.Contains("unreadable", text);
    }

    [Fact]
    public void Render_without_a_trace_says_nothing_about_hotkeys()
    {
        var text = DiagnosticsDump.Render(new FakeGigabyteWmi(), ModelProfile.Detect("AORUS 17G KD"), "0.1.0-test");

        Assert.DoesNotContain("Hotkey channels", text);
    }
}
