using Microsoft.Win32;

namespace OpenAorus.Hardware.Platform;

/// <summary>
/// A service's start mode, as needed to restore it exactly. Mirrors <see cref="System.ServiceProcess.ServiceStartMode"/>
/// except it adds <see cref="AutomaticDelayed"/>, which that type cannot represent (delayed auto-start is a
/// separate registry flag on top of "Automatic", not a distinct <c>SERVICE_START_TYPE</c> value).
/// </summary>
public enum GccServiceStartMode { Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }

/// <summary>OS actions needed to park Gigabyte Control Center. Real impl: <see cref="WindowsGccSystem"/>.</summary>
public interface IGccSystem
{
    bool TaskExists(string name);
    bool IsTaskEnabled(string name);
    bool DisableTask(string name);
    bool EnableTask(string name);
    string? ReadRunValue(string name);

    /// <summary>The registry kind of the run value, so a restore can write it back unchanged. Returns <see cref="RegistryValueKind.String"/> when the value does not exist or its kind cannot be determined.</summary>
    RegistryValueKind ReadRunValueKind(string name);
    void DeleteRunValue(string name);
    void WriteRunValue(string name, string value, RegistryValueKind kind);
    bool ServiceExists(string name);

    /// <summary>The service's start mode before anything here touches it, so a takeover can record it and a restore can write it back exactly.</summary>
    GccServiceStartMode GetServiceStartMode(string name);

    /// <summary>Whether the service is currently running, so a restore only restarts it if it was running before.</summary>
    bool IsServiceRunning(string name);

    /// <summary>Stops the service (if running) and sets its start mode to Disabled. Returns whether the disable itself succeeded - the caller must not record it as done otherwise.</summary>
    bool StopAndDisableService(string name);

    /// <summary>Restores the service to <paramref name="originalMode"/> and starts it only if <paramref name="wasRunning"/> is true.</summary>
    bool EnableService(string name, GccServiceStartMode originalMode, bool wasRunning);
    int KillProcesses(IEnumerable<string> names);
    bool AnyProcessRunning(IEnumerable<string> names);
}
