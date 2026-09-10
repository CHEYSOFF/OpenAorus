namespace OpenAorus.Hardware.Platform;

/// <summary>What one run of <c>mofcomp.exe</c> said.</summary>
/// <param name="ExitCode">The process exit code, or a negative number if it never ran, was killed
/// on a timeout, or failed in a way that produced no code of its own.</param>
/// <param name="Output">Everything the run wrote to stdout and stderr, verbatim. This is the only
/// thing in the system that can name the line and the qualifier a MOF was rejected for, so it is
/// carried into the owner-facing message rather than summarised away.</param>
/// <remarks>
/// A zero exit code is not proof that anything happened. <c>#pragma deleteclass ... NOFAIL</c>
/// exits zero having deleted nothing, and a compile can report success against a repository that
/// ends up without the classes. Every caller here re-reads the machine afterwards instead of
/// trusting <see cref="Success"/> on its own.
/// </remarks>
public sealed record MofCompResult(int ExitCode, string Output)
{
    /// <summary>Whether the process exited zero.</summary>
    public bool Success => ExitCode == 0;
}

/// <summary>
/// A machine's WMI repository and its copy of <c>mofcomp</c>. The whole of the registration
/// feature's OS-facing surface.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE SEAM. Only <c>WindowsSchemaSystem</c> may implement this against the real OS.
/// Nothing above it starts a process, opens a <c>ManagementScope</c>, or touches the file system:
/// everything above is decisions about what to run and what to conclude from it, and all of those
/// are exercised over a fake. A mofcomp that refuses, a mofcomp that reports success and creates
/// nothing, a machine where Control Center is already installed, a half-removed one - none of
/// those is reachable on real hardware without breaking the laptop.
/// </para>
/// <para>
/// NOTHING HERE MAY THROW except <see cref="WriteMofFile"/> and <see cref="DeleteMofFile"/>, which
/// touch the file system and whose failures have no in-band answer. A reader that cannot read
/// answers "no"; a run that could not start answers with a negative
/// <see cref="MofCompResult.ExitCode"/>. <see cref="Wmi.Schema.SchemaRegistrar"/> defends against
/// an implementation that breaks that contract anyway, but it is a contract and not a hope: an
/// exception out of a look is indistinguishable from an unreadable machine by the time it reaches
/// a caller, and an unreadable machine must never be registered over.
/// </para>
/// <para>
/// <see cref="MofCompPath"/> is a property rather than a method because "is <c>mofcomp</c> there"
/// is a fact about the machine, not an action.
/// </para>
/// </remarks>
public interface ISchemaSystem
{
    /// <summary>
    /// Where <c>mofcomp.exe</c> is, or null if it is not on this machine.
    /// </summary>
    /// <remarks><c>mofcomp</c> ships with Windows, so null means the WMI installation is damaged.
    /// Registering a schema on top of that is not the right repair, and both operations refuse.</remarks>
    string? MofCompPath { get; }

    /// <summary>Whether a class of this name resolves in the schema namespace.</summary>
    /// <param name="className">The class name.</param>
    /// <returns>True if the name resolves, whatever it declares. A class that is present and
    /// declares no methods is present: it is a name <c>mofcomp</c> would collide with.</returns>
    bool ClassExists(string className);

    /// <summary>Reads a class's method names and their <c>WmiMethodId</c> qualifiers.</summary>
    /// <param name="className">The class name.</param>
    /// <returns>Method name to id, empty for a class that declares none or could not be read.</returns>
    IReadOnlyDictionary<string, int> ReadMethodIds(string className);

    /// <summary>Reads the schema fingerprint our marker instance records.</summary>
    /// <returns>The fingerprint the registration wrote down when it was made, or null if there is
    /// no marker, it carries none, or it could not be read.</returns>
    /// <remarks>
    /// <para>
    /// This is the record of what was changed, and it lives in WMI rather than in the settings file
    /// on purpose: a settings file can be deleted while the classes stay behind, and a schema whose
    /// only record of itself is gone is a schema nobody can account for.
    /// </para>
    /// <para>
    /// It feeds the refusal messages, and exactly one decision:
    /// <see cref="Wmi.Schema.SchemaStatus.OursEmptied"/>, which needs this to record the schema our
    /// install file writes before it will permit a removal. Null - no marker, no record, or a read
    /// that failed - matches no fingerprint, so a marker that will not answer can only ever
    /// withhold that permission and never grant it.
    /// </para>
    /// </remarks>
    string? ReadMarkerFingerprint();

    /// <summary>Runs <c>mofcomp.exe</c> with these arguments and waits for it.</summary>
    /// <param name="arguments">The command line, already quoted.</param>
    /// <returns>What it said. A process that would not start, refused, or had to be killed on a
    /// timeout comes back as a failed result rather than an exception.</returns>
    MofCompResult RunMofComp(string arguments);

    /// <summary>Writes one MOF out to a temporary path so <c>mofcomp</c> has something to open.</summary>
    /// <param name="fileName">The file name to use.</param>
    /// <param name="content">The MOF text.</param>
    /// <returns>The full path written.</returns>
    /// <remarks>May throw: a directory that will not take a file has no in-band answer. An
    /// implementation that fails part-way must remove whatever it created before throwing, since
    /// the caller has no path to clean up.</remarks>
    string WriteMofFile(string fileName, string content);

    /// <summary>Deletes a file <see cref="WriteMofFile"/> returned.</summary>
    /// <param name="path">The path.</param>
    /// <remarks>May throw. The caller treats a failure here as housekeeping and not as a failed
    /// registration.</remarks>
    void DeleteMofFile(string path);
}
