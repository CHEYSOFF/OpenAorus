namespace OpenAorus.Hardware.Platform;

/// <summary>OS actions needed to park Gigabyte Control Center. Real impl: <see cref="WindowsGccSystem"/>.</summary>
public interface IGccSystem
{
    bool TaskExists(string name);
    bool IsTaskEnabled(string name);
    bool DisableTask(string name);
    bool EnableTask(string name);
    string? ReadRunValue(string name);
    void DeleteRunValue(string name);
    void WriteRunValue(string name, string value);
    bool ServiceExists(string name);
    bool StopAndDisableService(string name);
    bool EnableService(string name);
    int KillProcesses(IEnumerable<string> names);
    bool AnyProcessRunning(IEnumerable<string> names);
}
