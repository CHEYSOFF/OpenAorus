using Microsoft.Win32;

namespace OpenAorus.Hardware.Platform;

public sealed class GccTakeoverState
{
    public bool TaskDisabled { get; set; }
    public string? RunValue { get; set; }

    /// <summary>Registry kind the run value had before it was removed. Null in state produced by an older settings file; Restore then defaults to String.</summary>
    public RegistryValueKind? RunValueKind { get; set; }
    public bool ServiceDisabled { get; set; }
    public DateTime When { get; set; } = DateTime.Now;
}

/// <summary>Disables GCC's autostart pieces so two controllers do not fight over the EC. Fully reversible.</summary>
public static class GccTakeover
{
    public const string TaskName = "GCC";                 // \GCC, RunLevel Highest, created by GCC installer
    public const string RunValueName = "AorusFusion";     // HKLM\...\Run, legacy ControlCenter autostart
    public const string ServiceName = "SMV4_Service";     // legacy ControlCenter LocalSystem service

    public static readonly string[] ProcessNames =
    {
        "GCC", "ControlCenter", "FusionStation", "FusionShortcut", "OSDwindow",
        "CloudMatrixControlCenter", "GbtCloudMatrix", "GBT_DL_LIB", "LaunchGCC",
    };

    public static GccTakeoverState TakeOver(IGccSystem sys)
    {
        var state = new GccTakeoverState();
        if (sys.TaskExists(TaskName))
            state.TaskDisabled = sys.DisableTask(TaskName);

        var run = sys.ReadRunValue(RunValueName);
        if (run is not null)
        {
            state.RunValueKind = sys.ReadRunValueKind(RunValueName);
            sys.DeleteRunValue(RunValueName);
            state.RunValue = run;
        }

        if (sys.ServiceExists(ServiceName))
            state.ServiceDisabled = sys.StopAndDisableService(ServiceName);

        sys.KillProcesses(ProcessNames);
        return state;
    }

    public static void Restore(IGccSystem sys, GccTakeoverState state)
    {
        if (state.TaskDisabled) sys.EnableTask(TaskName);
        if (state.RunValue is not null) sys.WriteRunValue(RunValueName, state.RunValue, state.RunValueKind ?? RegistryValueKind.String);
        if (state.ServiceDisabled) sys.EnableService(ServiceName);
    }

    public static bool IsGccActive(IGccSystem sys) =>
        sys.AnyProcessRunning(ProcessNames)
        || sys.IsTaskEnabled(TaskName)
        || sys.ReadRunValue(RunValueName) is not null;
}
