using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace OpenAorus.Hardware.Platform;

public sealed class WindowsGccSystem : IGccSystem
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Runs a child process with a bounded wait. Never throws: a start failure, a timeout (the child
    /// is then killed) or any other error all come back as a failed (-1) result rather than escaping the seam.</summary>
    private static (int code, string output) Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, string.Empty);

            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return (-1, output);
            }
            return (p.ExitCode, output);
        }
        catch (Exception)
        {
            return (-1, string.Empty);
        }
    }

    public bool TaskExists(string name) => Run("schtasks", $"/Query /TN \"{name}\"").code == 0;

    public bool IsTaskEnabled(string name)
    {
        // XML output carries an unlocalized <Enabled>true|false</Enabled> element; the plain-text /FO LIST /V
        // output is localized (e.g. the word "Disabled" is translated), which would misreport on non-English Windows.
        var (code, output) = Run("schtasks", $"/Query /TN \"{name}\" /XML");
        return code == 0 && !output.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase);
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
        // DoNotExpandEnvironmentNames: a REG_EXPAND_SZ value must round-trip verbatim, not with %VARS% expanded.
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
    }

    public RegistryValueKind ReadRunValueKind(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: false);
        if (key is null) return RegistryValueKind.String;
        try { return key.GetValueKind(name); }
        catch (Exception) { return RegistryValueKind.String; }
    }

    public void DeleteRunValue(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void WriteRunValue(string name, string value, RegistryValueKind kind)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RunKey, writable: true);
        key.SetValue(name, value, kind);
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

    public bool AnyProcessRunning(IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            var procs = Process.GetProcessesByName(n);
            try
            {
                if (procs.Length > 0) return true;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        return false;
    }
}
