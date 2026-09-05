using Microsoft.Win32;

namespace OpenAorus.Hardware.Platform;

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
    bool StopAndDisableService(string name);
    bool EnableService(string name);
    int KillProcesses(IEnumerable<string> names);
    bool AnyProcessRunning(IEnumerable<string> names);
}
