using System.Text;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Hotkeys;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Diagnostics;

/// <summary>Reads every argument-less GB_WMIACPI_Get method so untested models can be profiled from a bug report.</summary>
public static class DiagnosticsDump
{
    public static readonly string[] GetMethods =
    {
        "GetCPUFanDuty", "GetGPUFanDuty", "GetBatteryCount", "GetMaxCharge", "GetPEGorSG", "GetNvPowerConfig",
        "GetThermalData", "GetNvThermalTarget", "CheckHeavyLoading", "GetDeepFan", "GetBatteryHealth", "GetFanHealth",
        "GetPowerOnTime", "GetChargePolicy", "GetChargeStop", "GetStepFanStatus", "GetVRStatus", "GetFixedFanStatus",
        "GetFixedFanSpeed", "IsDeviceExist", "getBattCyc1", "getBattCyc", "GetFanPWMStatus", "GetFanAdjustStatus",
        "GetAutoFanStatus", "GetSleepUSBCharge", "GetHibernationUSBCharge", "CheckUCFSupport", "GetFanSpeed",
        "GetCamera2", "GetTouchScreenSupport", "GetAIBoostStatus", "GetFirstDate", "GetEcValueBoostStatus",
        "GetWhisperMode", "GetBrightness", "GetBluetooth", "GetWiFi", "GetW35G", "GetBirightnessOff", "GetCamera",
        "GetTouchPad", "GetWinkeyBlocking", "GetTurboMode", "GetSmartCharge", "CheckSmartCharge", "CheckSmartTurbo",
        "GetUSB30", "CheckUSB30", "GetDockingStatus", "GetTouchScreen", "GetSmartTurbo", "GetLid1Status",
        "GetKeyboardMatrix", "GetSmartTurboStatus", "GetGSensorStatus", "GetOnboardLANStatus", "CheckDocking",
        "GetKeyBoardBackLight", "QueryLightSensor", "QueryThermalSensor", "GetSmartCool", "GetLightSensorVersion",
        "CheckBIOSMode", "Check3GModule", "getCpuTemp", "getGpuTemp1", "getGpuTemp2", "getRpm1", "getRpm2",
        "GetPEG2orSG2", "GetDynamicBoostStatus",
    };

    /// <summary>Renders the whole dump.</summary>
    /// <param name="wmi">The WMI channel to read through.</param>
    /// <param name="profile">The detected model.</param>
    /// <param name="appVersion">The running version.</param>
    /// <param name="hotkeys">What the hotkey channels have seen, if they are wired up. Near the
    /// top on purpose: when an Fn key does nothing, whether the report arrived at all is the
    /// first thing worth knowing, and the only thing that separates a misread report from a
    /// chassis that never sent one.
    ///
    /// ONLY THE DIAGNOSTICS BUTTON CAN PRODUCE A FULL ONE. The channels are opened from
    /// <c>MainViewModel</c> when the window comes up, so <c>--dump</c> - which exits before any
    /// window exists - renders this section with every count at zero and the line
    /// "nothing has arrived", every time, on every machine. That is character for character the
    /// reading <c>VERIFY.md</c> 7.1 defines as "this chassis emits nothing on these channels", so
    /// a dump exported from the command line is the easiest way there is to record a false
    /// negative about the hardware. The checklist leads with the in-window button for that reason;
    /// this section is only evidence when it came from there, after keys were actually
    /// pressed.</param>
    /// <param name="schema">The last reading of what is registered in <c>root\WMI</c>, if the
    /// caller has one. Rendered beside the hotkey trace and above the readings, because it decides
    /// whether any of them could have worked: on a machine with no schema every line below is
    /// <c>Not found</c>, and without this section nothing in the file says why.</param>
    /// <param name="gates">The gate record from the settings file, if the caller has one. The
    /// design and the checklist both treat a dump as the artefact of a gate run; the verdict
    /// otherwise lives only in a Settings card and in <c>settings.json</c>, neither of which
    /// reaches a bug report.</param>
    /// <returns>The dump text.</returns>
    public static string Render(
        IGigabyteWmi wmi, ModelProfile profile, string appVersion, HotkeyTrace? hotkeys = null,
        SchemaReport? schema = null, SchemaRecord? gates = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OpenAorus {appVersion} diagnostics - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Model: {profile.Name} ({profile.Status}, DutyMax={profile.DutyMax}, Fans={profile.FanCount})");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine();
        if (schema is not null)
        {
            AppendSchema(sb, schema, gates);
            sb.AppendLine();
        }
        if (hotkeys is not null)
        {
            sb.Append(hotkeys.Render());
            sb.AppendLine();
        }
        sb.AppendLine("GB_WMIACPI_Get:");
        foreach (var m in GetMethods)
        {
            var r = wmi.Get(m);
            sb.Append("  ").Append(m).Append(": ");
            sb.AppendLine(r.Success
                ? string.Join(" ", r.Out.Select(kv => $"{kv.Key}={kv.Value}"))
                : $"ERROR {r.Error}");
        }
        sb.AppendLine();
        sb.AppendLine("Fan table (GetFanIndexValue 0..14):");
        for (byte i = 0; i < 15; i++)
        {
            var r = wmi.Invoke(WmiClass.Get, "GetFanIndexValue", new Dictionary<string, object> { ["Index"] = i });
            sb.AppendLine(r.Success
                ? $"  [{i}] temp={r.GetInt("Temperture")} duty={r.GetInt("Value")}"
                : $"  [{i}] ERROR {r.Error}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Renders what is registered on this machine and what the gates made of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four lines, and every absence is printed as an answer rather than left blank. A machine
    /// with no schema is the one most likely to be exporting a dump, and "nothing is registered,
    /// nothing to fingerprint, no run recorded" is a complete reading of it - a blank there would
    /// read as a renderer that gave up.
    /// </para>
    /// <para>
    /// A machine that could not be READ is not the same as one with nothing registered, and the
    /// two are kept apart here exactly as <see cref="SchemaReport.Status"/> keeps them apart: one
    /// is a machine to register on, the other is a WMI service to fix first.
    /// </para>
    /// <para>
    /// The record's own fingerprint is printed beside the live one because that comparison is the
    /// whole of why a pass expires: a recorded pass over a mapping the machine no longer carries
    /// unlocks nothing, and two fingerprints that differ say so at a glance.
    /// </para>
    /// </remarks>
    private static void AppendSchema(StringBuilder sb, SchemaReport schema, SchemaRecord? gates)
    {
        var state = schema.Status is { } status ? status.ToString() : "could not be read";
        sb.AppendLine($"WMI schema: {state} - writes {(schema.WritesUnlocked ? "unlocked" : "locked")}");
        if (schema.Status is null && schema.Failure is { } failure)
            sb.AppendLine($"  Windows reported: {failure}");

        var live = schema.Snapshot is null ? "not read"
            : schema.Snapshot.LiveFingerprint ?? "none - no registered class declares a method";
        sb.AppendLine($"  live fingerprint: {live}");

        var pass = gates is { GatesPassed: true, When: { } when, Fingerprint: { } against }
            ? $"{when:yyyy-MM-dd HH:mm} against {against}"
            : "none";
        sb.AppendLine($"  recorded pass: {pass}");

        // Printed even when the pass it describes has been cleared or has expired: what a past run
        // reported is still the most useful thing an owner asking why writes are locked can send.
        var summary = gates?.GateSummary;
        sb.AppendLine($"  gate summary: {(string.IsNullOrWhiteSpace(summary) ? "none recorded" : summary)}");
    }
}
