using System.IO;
using System.Runtime.InteropServices;

namespace OpenAorus.App;

internal static class ConsoleAttach
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>Returns true if stdout now goes to the launching console.</summary>
    public static bool TryAttach()
    {
        if (!AttachConsole(AttachParentProcess)) return false;
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        return true;
    }
}
