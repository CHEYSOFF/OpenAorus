using System.Text;
using OpenAorus.Hardware.Hotkeys;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;

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
    /// chassis that never sent one.</param>
    /// <returns>The dump text.</returns>
    public static string Render(IGigabyteWmi wmi, ModelProfile profile, string appVersion, HotkeyTrace? hotkeys = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OpenAorus {appVersion} diagnostics - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Model: {profile.Name} ({profile.Status}, DutyMax={profile.DutyMax}, Fans={profile.FanCount})");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine();
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
}
