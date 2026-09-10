using OpenAorus.Hardware.Platform;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// A machine's WMI repository and its copy of mofcomp, as a dictionary and a queue.
/// </summary>
/// <remarks>
/// The point of the seam. Everything above <see cref="ISchemaSystem"/> is decisions about what to
/// run and what to conclude, and all of it is exercised here: a mofcomp that refuses, a mofcomp
/// that reports success and creates nothing, a machine where Control Center is already installed,
/// a half-removed one. None of that is reachable on real hardware without breaking the laptop.
/// </remarks>
public sealed class FakeSchemaSystem : ISchemaSystem
{
    /// <summary>The classes this machine holds. Mutated by <see cref="OnCompile"/>.</summary>
    public HashSet<string> Classes { get; } = new(StringComparer.Ordinal);

    /// <summary>Live method ids per class, for the fingerprint.</summary>
    public Dictionary<string, IReadOnlyDictionary<string, int>> MethodIds { get; } = new(StringComparer.Ordinal);

    /// <summary>What the marker instance records, when the marker class is there to carry it.</summary>
    public string? MarkerFingerprint { get; set; }

    /// <summary>Where mofcomp is, or null for a machine whose WMI installation is damaged.</summary>
    public string? MofCompPath { get; set; } = @"C:\Windows\System32\wbem\mofcomp.exe";

    /// <summary>Every argument string handed to mofcomp, in order.</summary>
    public List<string> MofCompCalls { get; } = new();

    /// <summary>Every file name written out, in order.</summary>
    public List<string> FilesWritten { get; } = new();

    /// <summary>Every path deleted, in order.</summary>
    public List<string> FilesDeleted { get; } = new();

    /// <summary>What mofcomp does, keyed by whether the arguments contain "-check".</summary>
    public Func<string, MofCompResult> OnCompile { get; set; } = _ => new MofCompResult(0, "");

    /// <summary>
    /// Thrown by <see cref="ClassExists"/>, for a seam that breaks its own no-throw contract.
    /// </summary>
    public Exception? LookFailure { get; set; }

    /// <summary>Thrown by <see cref="WriteMofFile"/>, for a temporary directory that will not take a file.</summary>
    public Exception? WriteFailure { get; set; }

    /// <summary>Thrown by <see cref="DeleteMofFile"/>, for a file that will not go away.</summary>
    public Exception? DeleteFailure { get; set; }

    /// <summary>Thrown by <see cref="ReadMarkerFingerprint"/>, which only ever feeds a message.</summary>
    public Exception? MarkerFailure { get; set; }

    /// <inheritdoc/>
    public bool ClassExists(string className) =>
        LookFailure is { } ex ? throw ex : Classes.Contains(className);

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, int> ReadMethodIds(string className) =>
        MethodIds.TryGetValue(className, out var ids) ? ids : new Dictionary<string, int>(StringComparer.Ordinal);

    /// <inheritdoc/>
    public string? ReadMarkerFingerprint() =>
        MarkerFailure is { } ex ? throw ex
        : Classes.Contains(MofWriter.MarkerClass) ? MarkerFingerprint
        : null;

    /// <inheritdoc/>
    public MofCompResult RunMofComp(string arguments)
    {
        MofCompCalls.Add(arguments);
        return OnCompile(arguments);
    }

    /// <inheritdoc/>
    public string WriteMofFile(string fileName, string content)
    {
        if (WriteFailure is { } ex) throw ex;

        FilesWritten.Add(fileName);
        return $@"C:\fake\{fileName}";
    }

    /// <inheritdoc/>
    public void DeleteMofFile(string path)
    {
        FilesDeleted.Add(path);
        if (DeleteFailure is { } ex) throw ex;
    }

    /// <summary>A mofcomp that actually registers the five classes, for the happy path.</summary>
    /// <param name="fingerprint">What the marker instance records once the install file compiles.</param>
    /// <param name="ids">The method ids the compiled classes end up declaring.</param>
    public void CompileForReal(string fingerprint, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ids) =>
        OnCompile = args =>
        {
            if (args.Contains("-check", StringComparison.Ordinal)) return new MofCompResult(0, "syntax ok");
            if (args.Contains("remove", StringComparison.Ordinal))
            {
                Classes.Clear();
                MethodIds.Clear();
                MarkerFingerprint = null;
                return new MofCompResult(0, "deleted");
            }
            foreach (var n in SchemaClasses.All) Classes.Add(n);
            foreach (var (k, v) in ids) MethodIds[k] = v;
            MarkerFingerprint = fingerprint;
            return new MofCompResult(0, "compiled");
        };
}
