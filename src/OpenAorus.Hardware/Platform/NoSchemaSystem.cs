namespace OpenAorus.Hardware.Platform;

/// <summary>An <see cref="ISchemaSystem"/> for a process with no WMI repository behind it.</summary>
/// <remarks>
/// <para>
/// The same null object <c>NoPanelBrightness</c> is, and for the same reason: a rig that has no
/// interest in the schema still has to hand something to the code that reads it, and a null there
/// would put a check on every call site.
/// </para>
/// <para>
/// It answers "nothing is registered" and "there is no compiler", which is the pair that makes both
/// <see cref="Wmi.Schema.SchemaRegistrar"/> operations refuse before they have touched anything.
/// <see cref="WriteMofFile"/> throws rather than returning a path, because a path this object
/// invented would be handed to a compiler that is not there.
/// </para>
/// </remarks>
public sealed class NoSchemaSystem : ISchemaSystem
{
    private static readonly IReadOnlyDictionary<string, int> NoMethods =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <inheritdoc/>
    public string? MofCompPath => null;

    /// <inheritdoc/>
    public bool ClassExists(string className) => false;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, int> ReadMethodIds(string className) => NoMethods;

    /// <inheritdoc/>
    public string? ReadMarkerFingerprint() => null;

    /// <inheritdoc/>
    public MofCompResult RunMofComp(string arguments) =>
        new(-1, "There is no WMI repository behind this process, so mofcomp was never run.");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always.</exception>
    public string WriteMofFile(string fileName, string content) =>
        throw new NotSupportedException("There is no machine behind this process to register a schema on.");

    /// <inheritdoc/>
    public void DeleteMofFile(string path)
    {
        // Nothing was written, so there is nothing to take back, and a housekeeping step that
        // threw would be the one failure this class could cause on its own.
    }
}
