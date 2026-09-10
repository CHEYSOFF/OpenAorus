using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.CompilerServices;
using System.Text;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Wmi.Schema;

// So the two failures that matter most here can be provoked in a test. A compiler that is not on
// the machine and a run that will not finish both go wrong on the owner's laptop and nowhere else,
// and both must come back as a result rather than as an exception - which is the one thing about
// this file that is neither obvious nor visible from outside it.
[assembly: InternalsVisibleTo("OpenAorus.Hardware.Tests")]

namespace OpenAorus.Hardware.Platform;

/// <summary>
/// <see cref="ISchemaSystem"/> against the machine this process is running on.
/// </summary>
/// <remarks>
/// <para>
/// THE WHOLE OF THE UNTESTABLE PART, AND DELIBERATELY SMALL. Everything that decides anything -
/// what to compile, what to conclude from a run, whether a registration is ours - is above the
/// seam and tested over a fake. What is left here is: resolve a path, start a process, read class
/// metadata, write two files.
/// </para>
/// <para>
/// NOTHING HERE THROWS except <see cref="WriteMofFile"/> and <see cref="DeleteMofFile"/>. A read
/// that fails answers "no"; a run that fails answers with a negative exit code and whatever
/// Windows said. The reason is not tidiness: an exception out of a look is indistinguishable from
/// an unreadable machine by the time it reaches <see cref="SchemaRegistrar"/>, and an unreadable
/// machine must never be registered over.
/// </para>
/// <para>
/// <c>mofcomp.exe</c> is resolved absolutely and never taken off <c>PATH</c>. This process is
/// elevated, and starting a bare-named executable from an elevated process lets anything that can
/// write a <c>PATH</c> directory decide what runs.
/// </para>
/// </remarks>
public sealed class WindowsSchemaSystem : ISchemaSystem
{
    /// <summary>
    /// How long a single <c>mofcomp</c> run is given before it is killed.
    /// </summary>
    /// <remarks>Sixty seconds, not fifteen. <c>mofcomp</c> against a cold WMI repository can take
    /// a while, and killing it half way through creating a class is the one reliable way to reach
    /// the half-registered state the whole design refuses to allow.</remarks>
    public const int TimeoutMs = 60_000;

    /// <summary>The qualifier that carries the firmware method id our MOF wrote.</summary>
    private const string MethodIdQualifier = "WmiMethodId";

    /// <summary>How long the output readers are given to finish after the process has exited.</summary>
    /// <remarks>The process is already gone by then; this only covers the handful of milliseconds
    /// between its exit and the pipes reaching end of stream. A grandchild holding an inherited
    /// handle open would otherwise hang the caller here instead of at the timeout.</remarks>
    private const int DrainMs = 5_000;

    // mofcomp reads ANSI or UTF-16-with-BOM. The MOF text is ASCII, so UTF-8 without a BOM is the
    // same bytes ANSI would be - and a stray UTF-8 BOM is one of the few ways a byte-correct file
    // still fails to parse.
    private static readonly UTF8Encoding MofEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Where Windows keeps <c>mofcomp.exe</c>, whether or not it is actually there.</summary>
    /// <remarks>Read off <see cref="Environment.SpecialFolder.System"/> rather than assumed to be
    /// under <c>C:\Windows</c>, and fully qualified so nothing is ever resolved through
    /// <c>PATH</c>.</remarks>
    public static string ExpectedMofCompPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wbem", "mofcomp.exe");

    /// <summary>
    /// Where the MOFs are written out for <c>mofcomp</c> to open.
    /// </summary>
    /// <remarks>Beside <c>settings.json</c>, not in <c>%TEMP%</c>. A file an elevated process is
    /// about to feed to a compiler should live somewhere a non-elevated process on the machine
    /// cannot replace between the write and the read, and the app's own LocalAppData directory is
    /// already the place it trusts. Derived from <see cref="SettingsStore.DefaultPath"/> so the two
    /// cannot drift apart.</remarks>
    public static string MofDirectory =>
        Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath)!, "schema");

    /// <inheritdoc/>
    public string? MofCompPath
    {
        get
        {
            var path = ExpectedMofCompPath;
            return File.Exists(path) ? path : null;
        }
    }

    /// <inheritdoc/>
    public bool ClassExists(string className)
    {
        try
        {
            using var declared = OpenClass(className);
            return true;
        }
        catch (Exception)
        {
            // ManagementException for a name that does not resolve, and anything else for a
            // repository this process cannot open. Both mean the same thing to every caller:
            // there is no class of that name here to be collided with.
            return false;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, int> ReadMethodIds(string className)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            using var declared = OpenClass(className);

            foreach (MethodData method in declared.Methods)
            {
                // A method carrying no WmiMethodId is skipped rather than recorded under a
                // sentinel: it is not part of the mapping, and a stand-in value would make the
                // fingerprint depend on how WMI reports an absence.
                if (MethodId(method) is { } id) ids[method.Name] = id;
            }
        }
        catch (Exception)
        {
            // Half a class read is worse than none: it fingerprints as a mapping nobody installed,
            // which reads as another program's registration and is then refused rather than acted on.
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        return ids;
    }

    /// <inheritdoc/>
    public string? ReadMarkerFingerprint()
    {
        try
        {
            var path = new ManagementPath(
                $"{MofWriter.MarkerClass}.Id=\"{MofWriter.MarkerInstanceId}\"");

            using var marker = new ManagementObject(Connected(), path, null);
            marker.Get();

            return marker["Fingerprint"] as string;
        }
        catch (Exception)
        {
            // No marker, no instance, or no readable repository. This only ever feeds a sentence
            // in a message, so "it did not say" is the whole of the answer.
            return null;
        }
    }

    /// <inheritdoc/>
    public MofCompResult RunMofComp(string arguments)
    {
        if (MofCompPath is not { } exePath)
        {
            return new MofCompResult(
                -1, "mofcomp.exe is not at " + ExpectedMofCompPath + " on this machine.");
        }

        return Run(exePath, arguments, TimeoutMs);
    }

    /// <inheritdoc/>
    public string WriteMofFile(string fileName, string content)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        // Path.Combine would happily let an absolute or traversing name out of the directory
        // whose permissions are the entire reason the files live there.
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{fileName}' is not a bare file name. The MOF an elevated compiler is about to " +
                "read must come from the app's own directory and nowhere else.", nameof(fileName));
        }

        Directory.CreateDirectory(MofDirectory);
        var path = Path.Combine(MofDirectory, fileName);

        try
        {
            File.WriteAllText(path, content, MofEncoding);
        }
        catch (Exception)
        {
            // The caller has no path to clean up, so a half-written file would be left for
            // whatever opens it next - and what opens MOFs here runs elevated.
            try { File.Delete(path); } catch (Exception) { }
            throw;
        }

        return path;
    }

    /// <inheritdoc/>
    public void DeleteMofFile(string path) => File.Delete(path);

    /// <summary>
    /// Starts a child process, reads both of its streams and waits for it.
    /// </summary>
    /// <param name="exePath">The fully qualified executable.</param>
    /// <param name="arguments">Its command line, already quoted.</param>
    /// <param name="timeoutMs">How long to wait before killing it.</param>
    /// <returns>Its exit code and everything it wrote, or a negative code and an explanation.</returns>
    /// <remarks>
    /// <para>
    /// Internal rather than private so a compiler that is not on the machine and a run that will
    /// not finish can be provoked in a test. Neither is reachable through
    /// <see cref="RunMofComp"/> on a working Windows box, and both are exactly the shapes that
    /// must not escape as exceptions.
    /// </para>
    /// <para>
    /// The streams are read asynchronously and the wait is bounded. Reading one to the end and
    /// then the other - which is what the rest of this namespace does, for output far too small
    /// to matter - would block in the read if the child filled the pipe it was not being read
    /// from, and the timeout would never be reached at all.
    /// </para>
    /// </remarks>
    internal static MofCompResult Run(string exePath, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return new MofCompResult(-1, $"Windows started no process for {exePath}.");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                // The tree, because a compiler stuck on a repository operation may have children
                // of its own, and leaving one running is how the next attempt finds a lock.
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }

                // Observed and dropped. The kill closes the pipes, and a reader left faulted with
                // nobody looking surfaces later as an unobserved task exception, out of a run that
                // has already been reported and explained.
                _ = Drain(stdout);
                _ = Drain(stderr);

                return new MofCompResult(-1, TimedOut(timeoutMs));
            }

            return new MofCompResult(process.ExitCode, (Drain(stdout) + Drain(stderr)).Trim());
        }
        catch (Exception ex)
        {
            // Win32Exception for a binary that is not there or a launch that was refused, and
            // anything else for the rest. What Windows said is the only useful part, so it is
            // carried rather than replaced with a message of our own.
            return new MofCompResult(-1, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string TimedOut(int timeoutMs) =>
        "mofcomp did not finish within " +
        (timeoutMs / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) +
        " seconds and was stopped.";

    /// <summary>Takes what a reader collected, or nothing if it will not finish or faulted.</summary>
    /// <remarks>Losing the output is a worse message; losing the exit code by throwing here would
    /// be a worse answer.</remarks>
    private static string Drain(Task<string> reader)
    {
        try { return reader.Wait(DrainMs) ? reader.Result : string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    /// <summary>Reads a class's metadata. Nothing is invoked, so this cannot reach the firmware.</summary>
    /// <remarks>Throws if the name does not resolve or the repository will not open; every caller
    /// in this file turns that into an absence.</remarks>
    private static ManagementClass OpenClass(string className)
    {
        var declared = new ManagementClass(Connected(), new ManagementPath(className), null);
        try
        {
            declared.Get();
        }
        catch (Exception)
        {
            declared.Dispose();
            throw;
        }

        return declared;
    }

    /// <summary>The schema namespace, connected under the same terms the invoker uses.</summary>
    /// <remarks>Matching <see cref="Wmi.GigabyteWmi"/> on purpose: this reads what that will later
    /// call, and a namespace one half can open and the other cannot is not a distinction worth
    /// inventing here.</remarks>
    private static ManagementScope Connected()
    {
        var scope = new ManagementScope(
            MofWriter.Namespace,
            new ConnectionOptions
            {
                EnablePrivileges = true,
                Impersonation = ImpersonationLevel.Impersonate,
            });

        scope.Connect();
        return scope;
    }

    private static int? MethodId(MethodData method)
    {
        foreach (QualifierData qualifier in method.Qualifiers)
        {
            if (!string.Equals(qualifier.Name, MethodIdQualifier, StringComparison.OrdinalIgnoreCase))
                continue;

            // The MOF writes it unquoted, but a repository can hand it back as a string, so it is
            // converted rather than cast.
            try { return Convert.ToInt32(qualifier.Value, CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        return null;
    }
}
