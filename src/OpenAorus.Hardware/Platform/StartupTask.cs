using System.Diagnostics;

namespace OpenAorus.Hardware.Platform;

/// <summary>
/// "Start with Windows" via a logon task with RunLevel Highest, so the tray app starts elevated without a UAC prompt.
/// Same mechanism GCC and G-Helper use. Creating/deleting the task itself requires elevation.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "OpenAorus";

    public static string BuildCreateArguments(string exePath) =>
        $"/Create /SC ONLOGON /RL HIGHEST /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --tray\" /F";

    public static string BuildDeleteArguments() => $"/Delete /TN \"{TaskName}\" /F";

    public static string BuildQueryArguments() => $"/Query /TN \"{TaskName}\"";

    public static bool IsEnabled() => RunSchtasks(BuildQueryArguments()) == 0;

    public static bool Enable(string exePath) => RunSchtasks(BuildCreateArguments(exePath)) == 0;

    public static bool Disable() => RunSchtasks(BuildDeleteArguments()) == 0;

    private static int RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch (Exception) { return -1; }
    }
}
