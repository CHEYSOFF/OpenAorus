using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace OpenAorus.Hardware.Platform;

public sealed class WindowsGccSystem : IGccSystem
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static (int code, string output) Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, output);
    }

    public bool TaskExists(string name) => Run("schtasks", $"/Query /TN \"{name}\"").code == 0;

    public bool IsTaskEnabled(string name)
    {
        var (code, output) = Run("schtasks", $"/Query /TN \"{name}\" /FO LIST /V");
        return code == 0 && !output.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
    }

    public bool DisableTask(string name)
    {
        var wasEnabled = IsTaskEnabled(name);
        return Run("schtasks", $"/Change /TN \"{name}\" /Disable").code == 0 && wasEnabled;
    }

    public bool EnableTask(string name) => Run("schtasks", $"/Change /TN \"{name}\" /Enable").code == 0;

    public string? ReadRunValue(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(name)?.ToString();
    }

    public void DeleteRunValue(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void WriteRunValue(string name, string value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RunKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public bool ServiceExists(string name) =>
        ServiceController.GetServices().Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool StopAndDisableService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            var wasEnabled = sc.StartType != ServiceStartMode.Disabled;
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            Run("sc", $"config \"{name}\" start= disabled");
            return wasEnabled;
        }
        catch (Exception) { return false; }
    }

    public bool EnableService(string name)
    {
        var ok = Run("sc", $"config \"{name}\" start= auto").code == 0;
        Run("sc", $"start \"{name}\"");
        return ok;
    }

    public int KillProcesses(IEnumerable<string> names)
    {
        var killed = 0;
        foreach (var n in names)
            foreach (var p in Process.GetProcessesByName(n))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); killed++; }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
        return killed;
    }

    public bool AnyProcessRunning(IEnumerable<string> names) =>
        names.Any(n => Process.GetProcessesByName(n).Length > 0);
}
