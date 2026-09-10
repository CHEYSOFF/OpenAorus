using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Diagnostics;
using OpenAorus.Hardware.Hotkeys;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi.Schema;

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

    /// <summary>A machine carrying a registration whose gates have both passed.</summary>
    private static SchemaReport Proved(string fingerprint) => new(
        SchemaStatus.OursGated,
        new SchemaSnapshot(true, true, true, true, true, fingerprint, fingerprint),
        Failure: null);

    [Fact]
    public void Render_carries_the_schema_state_the_gate_run_left_behind()
    {
        // The design and the checklist both call a dump the artefact of a gate run. Until this it
        // was not one: the verdict lived in a Settings card and in settings.json, so the file an
        // owner attaches to a bug report could not say why writes were locked or what was proved.
        var record = new SchemaRecord
        {
            Registered = true,
            GatesPassed = true,
            Fingerprint = "f00dcafe",
            When = new DateTime(2026, 9, 10, 21, 14, 0),
            GateSummary = "Gate A: 72 of 72 readings matched; Gate B: charge stop 80 % written back",
        };

        var text = DiagnosticsDump.Render(
            new FakeGigabyteWmi(), ModelProfile.Detect("AORUS 17G KD"), "0.1.0-test",
            schema: Proved("f00dcafe"), gates: record);

        Assert.Contains("WMI schema: OursGated", text);
        Assert.Contains("writes unlocked", text);
        Assert.Contains("live fingerprint: f00dcafe", text);
        Assert.Contains("2026-09-10 21:14", text);
        Assert.Contains("Gate A: 72 of 72 readings matched", text);

        // Above the 72 readings it explains, beside the hotkey trace, rather than under them.
        Assert.True(
            text.IndexOf("WMI schema:", StringComparison.Ordinal) <
            text.IndexOf("GB_WMIACPI_Get:", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_on_a_machine_with_no_schema_says_that_rather_than_leaving_blanks()
    {
        // The machine most likely to be exporting one: nothing registered, so nothing to
        // fingerprint and no gate run to report. Every one of those absences is the answer.
        var absent = new SchemaReport(
            SchemaStatus.Absent,
            new SchemaSnapshot(false, false, false, false, false, null, null),
            Failure: null);

        var text = DiagnosticsDump.Render(
            new FakeGigabyteWmi(), ModelProfile.Detect("AORUS 17G KD"), "0.1.0-test",
            schema: absent, gates: new SchemaRecord());

        Assert.Contains("WMI schema: Absent", text);
        Assert.Contains("writes locked", text);
        Assert.Contains("live fingerprint: none", text);
        Assert.Contains("recorded pass: none", text);
        Assert.Contains("gate summary: none recorded", text);
        Assert.DoesNotContain("null", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_says_when_the_repository_would_not_answer_at_all()
    {
        // Not the same as "nothing is registered", and the dump is where the difference has to
        // survive: one is a machine to register on, the other is a WMI service to fix first.
        var unreadable = new SchemaReport(
            Status: null, Snapshot: null, Failure: "GB_WMIACPI_Get: Invalid namespace");

        var text = DiagnosticsDump.Render(
            new FakeGigabyteWmi(), ModelProfile.Detect("AORUS 17G KD"), "0.1.0-test",
            schema: unreadable, gates: new SchemaRecord());

        Assert.Contains("WMI schema: could not be read", text);
        Assert.Contains("Invalid namespace", text);
        Assert.Contains("writes locked", text);
    }

    [Fact]
    public void Render_without_a_schema_reading_says_nothing_about_it()
    {
        var text = DiagnosticsDump.Render(
            new FakeGigabyteWmi(), ModelProfile.Detect("AORUS 17G KD"), "0.1.0-test");

        Assert.DoesNotContain("WMI schema", text);
    }

    [Fact]
    public void Render_without_a_trace_says_nothing_about_hotkeys()
    {
        var text = DiagnosticsDump.Render(new FakeGigabyteWmi(), ModelProfile.Detect("AORUS 17G KD"), "0.1.0-test");

        Assert.DoesNotContain("Hotkey channels", text);
    }
}
