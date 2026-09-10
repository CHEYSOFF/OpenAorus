using System.IO;
using System.Text;

namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>
/// The two generated MOF files, as they were checked in, read back out of the assembly.
/// </summary>
/// <remarks>
/// <para>
/// WHY THEY ARE CHECKED IN RATHER THAN GENERATED AT BUILD TIME. A MOF maps names to numbers, and
/// the entire point of the design is that a human can read the 143 numbers before they reach the
/// controller that governs cooling and charging. A file generated into <c>obj/</c> is a file
/// nobody reads. <c>MofDriftTests</c> reprints both on every test run and fails if either has
/// drifted from the research file by so much as a space, so checking them in costs nothing in
/// trust and buys a reviewable diff.
/// </para>
/// <para>
/// WHY THEY ARE EMBEDDED RATHER THAN COPIED BESIDE THE EXE. <c>mofcomp</c> takes a path on disk,
/// and a single-file publish has no loose files to point it at. They travel inside the assembly
/// and are written out to a temporary path at registration time.
/// </para>
/// <para>
/// <see cref="Fingerprint"/> is read out of <see cref="Install"/> rather than recomputed, so the
/// string the app compares a machine against is the string carried by the file it is about to
/// compile - not one arrived at separately that happens to agree today.
/// </para>
/// </remarks>
public static class SchemaMof
{
    /// <summary>The name the install file is written out under before <c>mofcomp</c> reads it.</summary>
    /// <remarks>It lands in a shared temporary directory. Named for us rather than for the
    /// classes it declares, because a <c>GB_WMIACPI.mof</c> sitting in <c>%TEMP%</c> is
    /// indistinguishable from Gigabyte's own.</remarks>
    public const string InstallFileName = "OpenAorus-GB_WMIACPI.mof";

    /// <summary>The name the remove file is written out under.</summary>
    public const string RemoveFileName = "OpenAorus-GB_WMIACPI-remove.mof";

    private const string InstallResource = "OpenAorus.Hardware.Wmi.Schema.GB_WMIACPI.mof";
    private const string RemoveResource = "OpenAorus.Hardware.Wmi.Schema.GB_WMIACPI-remove.mof";

    private const string FingerprintPrefix = "Fingerprint = \"";

    private static readonly Lazy<string> s_install = new(() => Read(InstallResource));
    private static readonly Lazy<string> s_remove = new(() => Read(RemoveResource));
    private static readonly Lazy<string> s_fingerprint = new(() => ReadFingerprint(Install));

    /// <summary>The MOF that declares the recovered classes and writes the marker instance.</summary>
    /// <exception cref="InvalidOperationException">The resource is not in the assembly.</exception>
    public static string Install => s_install.Value;

    /// <summary>The MOF that deletes them again.</summary>
    /// <exception cref="InvalidOperationException">The resource is not in the assembly.</exception>
    public static string Remove => s_remove.Value;

    /// <summary>
    /// The schema fingerprint <see cref="Install"/> records on its marker instance.
    /// </summary>
    /// <exception cref="InvalidOperationException">The install file carries no fingerprint. That
    /// would mean the marker instance registers without one, and every later comparison against a
    /// live machine would have nothing to compare.</exception>
    public static string Fingerprint => s_fingerprint.Value;

    private static string Read(string resourceName)
    {
        using var stream = typeof(SchemaMof).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The generated MOF '{resourceName}' is not embedded in this assembly. Without it " +
                "there is nothing to hand mofcomp, and registration cannot proceed.");

        // detectEncodingFromByteOrderMarks strips a BOM if one crept in. mofcomp reads ANSI or
        // UTF-16-with-BOM, so a stray UTF-8 BOM is one of the few ways a byte-correct file still
        // fails to parse - and it would also break the byte-for-byte drift comparison.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ReadFingerprint(string install)
    {
        var start = install.IndexOf(FingerprintPrefix, StringComparison.Ordinal);
        var end = start < 0 ? -1 : install.IndexOf('"', start + FingerprintPrefix.Length);

        if (start < 0 || end < 0)
        {
            throw new InvalidOperationException(
                $"The embedded install MOF carries no '{FingerprintPrefix}...\"' line. The marker " +
                "instance is what tells a later run that a registration is ours and which schema it " +
                "was, so a file without one must not be compiled.");
        }

        return install[(start + FingerprintPrefix.Length)..end];
    }
}
