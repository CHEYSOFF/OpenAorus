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

    /// <summary>
    /// Refuses to register the logon task unless <paramref name="exePath"/> is the published OpenAorus.exe.
    /// Under `dotnet run`, <c>Environment.ProcessPath</c> is the .NET host, so a task built from it points at
    /// dotnet.exe rather than the app - the task itself creates successfully but the tray icon never returns
    /// after logon, because dotnet.exe with no arguments does nothing useful.
    /// </summary>
    public static bool Enable(string exePath)
    {
        if (!IsPublishedExe(exePath)) return false;
        return RunSchtasks(BuildCreateArguments(exePath)) == 0;
    }

    /// <summary>True when <paramref name="exePath"/> is the published OpenAorus.exe rather than, e.g., the
    /// .NET host path `dotnet run` supplies. Exposed so callers can explain a refusal before even trying schtasks.</summary>
    public static bool IsPublishedExe(string exePath) =>
        exePath.EndsWith("OpenAorus.exe", StringComparison.OrdinalIgnoreCase);

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
            using var p = Process.Start(psi);
            if (p is null) return -1;

            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return -1;
            }
            return p.ExitCode;
        }
        catch (Exception) { return -1; }
    }
}
