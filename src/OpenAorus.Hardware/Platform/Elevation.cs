using System.Diagnostics;
using System.Security.Principal;

namespace OpenAorus.Hardware.Platform;

public static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts a new elevated copy (UAC prompt). Returns false if the user cancelled.</summary>
    public static bool RelaunchElevated(string exePath, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
        };
        try { Process.Start(psi); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; } // ERROR_CANCELLED
    }
}
