namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>What WMI answered when it was asked about one class.</summary>
/// <remarks>
/// Three answers, not two. "Present" and "absent" are both readings of a healthy repository;
/// "unreadable" is the repository declining to be read, and it is neither. Collapsing it into
/// "absent" - the obvious shortcut, since both mean "we did not get any methods" - is how a
/// machine whose WMI service is mid-rebuild ends up being registered over.
/// </remarks>
public sealed class WmiClassReading
{
    private WmiClassReading(IReadOnlyDictionary<string, int>? methodIds, string? failure)
    {
        MethodIds = methodIds;
        Failure = failure;
    }

    /// <summary>The class is not on this machine.</summary>
    public static WmiClassReading Absent { get; } = new(methodIds: null, failure: null);

    /// <summary>The class resolves and declares these methods on these <c>WmiMethodId</c>s.</summary>
    /// <param name="methodIds">Method name to <c>WmiMethodId</c>. Empty for a class that declares
    /// none, which is a present class and not an absent one.</param>
    /// <returns>The reading.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="methodIds"/> is null.</exception>
    public static WmiClassReading Present(IReadOnlyDictionary<string, int> methodIds)
    {
        ArgumentNullException.ThrowIfNull(methodIds);
        return new WmiClassReading(methodIds, failure: null);
    }

    /// <summary>WMI could not say. Neither present nor absent.</summary>
    /// <param name="reason">What Windows reported, for the diagnostics dump and the banner.</param>
    /// <returns>The reading.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is null.</exception>
    public static WmiClassReading Unreadable(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new WmiClassReading(methodIds: null, reason);
    }

    /// <summary>The declared methods, or null when the class is absent or unreadable.</summary>
    public IReadOnlyDictionary<string, int>? MethodIds { get; }

    /// <summary>Why the class could not be read, or null when it could.</summary>
    public string? Failure { get; }

    /// <summary>Whether the class name resolves, whatever it declares.</summary>
    public bool IsPresent => MethodIds is not null;

    /// <summary>Whether WMI declined to answer.</summary>
    public bool IsUnreadable => Failure is not null;
}

/// <summary>Reads WMI class metadata. The whole of this feature's OS-facing surface.</summary>
/// <remarks>
/// It reads class <em>definitions</em> and nothing else. There is deliberately no way to enumerate
/// an instance or invoke a method through this seam: instance enumeration is the operation that
/// reaches the firmware, and this runs at startup to decide what the window shows.
/// </remarks>
public interface IWmiClassSource
{
    /// <summary>Asks WMI about one class.</summary>
    /// <param name="className">The class name, in the reader's namespace.</param>
    /// <returns>Present with its method ids, absent, or unreadable with a reason.</returns>
    WmiClassReading Read(string className);
}

/// <summary>One reading of the machine, and what may be done to it.</summary>
/// <param name="Status">The state, or null if the machine could not be read at all - see
/// <paramref name="Failure"/>. Null is not a sixth state; it is the absence of an answer, and
/// every permission below is false while it lasts.</param>
/// <param name="Snapshot">What was found, or null alongside a null <paramref name="Status"/>.</param>
/// <param name="Failure">What Windows reported, or null if the machine was read cleanly.</param>
public sealed record SchemaReport(SchemaStatus? Status, SchemaSnapshot? Snapshot, string? Failure)
{
    /// <summary>Whether the machine could be read at all.</summary>
    public bool Known => Status is not null;

    /// <summary>Whether the Install button is offered.</summary>
    public bool CanInstall => Status is { } status && SchemaState.CanInstall(status);

    /// <summary>Whether the Remove button is offered.</summary>
    public bool CanRemove => Status is { } status && SchemaState.CanRemove(status);

    /// <summary>Whether fan and battery writes are allowed.</summary>
    public bool WritesUnlocked => Status is { } status && SchemaState.WritesUnlocked(status);

    /// <summary>Owner-facing prose saying what the machine is in and what will happen about it.</summary>
    public string Explanation => Status is { } status
        ? SchemaState.Explain(status)
        : "OpenAorus could not read which WMI classes are registered on this machine, so it will " +
          "neither register nor remove anything and fan and battery writes stay disabled. This is " +
          "usually a WMI service that is repairing its repository or is not running. Windows " +
          $"reported: {Failure}";
}

/// <summary>
/// Asks the machine what is registered, and never throws doing it.
/// </summary>
/// <remarks>
/// <para>
/// This is the join between the seam and <see cref="SchemaState"/>: it gathers five class readings,
/// renders the live fingerprint from the two that carry methods, and hands the result to
/// <see cref="SchemaState.Classify"/>. All of the decision-making is over there and is pure; all
/// of the OS is behind <see cref="IWmiClassSource"/>; this is the part that turns one into the
/// other.
/// </para>
/// <para>
/// ONE UNREADABLE CLASS STOPS EVERYTHING. A repository that answered for four names and errored on
/// the fifth has not said what is on the machine, and the two decisions downstream - compile a MOF,
/// delete classes by Gigabyte's names - are both irreversible enough that a guess is not worth
/// making. The reading comes back unknown, both buttons are withheld, and the owner is told what
/// Windows said.
/// </para>
/// </remarks>
public static class SchemaProbe
{
    /// <summary>Reads the machine and classifies what it finds.</summary>
    /// <param name="source">The seam onto WMI. Exceptions out of it are caught and reported, not
    /// propagated.</param>
    /// <param name="expectedFingerprint">The fingerprint our install file records - normally
    /// <see cref="SchemaMof.Fingerprint"/>.</param>
    /// <param name="gatesRecorded">Whether both hardware gates have been recorded as passed.</param>
    /// <returns>The reading, with a null <see cref="SchemaReport.Status"/> if the machine could not
    /// be read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or
    /// <paramref name="expectedFingerprint"/> is null.</exception>
    public static SchemaReport Read(IWmiClassSource source, string expectedFingerprint, bool gatesRecorded)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);

        var readings = new Dictionary<string, WmiClassReading>(StringComparer.Ordinal);

        foreach (var className in SchemaClasses.All)
        {
            readings[className] = ReadOne(source, className);

            // No early exit on the first unreadable class: the diagnostics dump is the only window
            // the owner has into this, and "all five failed the same way" reads very differently
            // from "one did".
        }

        foreach (var className in SchemaClasses.All)
        {
            if (readings[className].Failure is { } failure)
                return new SchemaReport(Status: null, Snapshot: null, $"{className}: {failure}");
        }

        var live = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var className in SchemaClasses.MethodBearing)
        {
            // A present class goes in even when it declares nothing, and renders as
            // SchemaFingerprint.Missing. Dropping it would let a repository that lost every method
            // off GB_WMIACPI_Get fingerprint as though the class had never been there.
            if (readings[className].MethodIds is { } methodIds)
                live[className] = methodIds;
        }

        var snapshot = new SchemaSnapshot(
            GetPresent: readings[WmiSchemaParser.GetClass].IsPresent,
            SetPresent: readings[WmiSchemaParser.SetClass].IsPresent,
            DataPresent: readings[WmiSchemaParser.DataClass].IsPresent,
            EventPresent: readings[WmiSchemaParser.EventClass].IsPresent,
            MarkerPresent: readings[MofWriter.MarkerClass].IsPresent,
            LiveFingerprint: live.Count == 0 ? null : SchemaFingerprint.OfLive(live));

        return new SchemaReport(
            SchemaState.Classify(snapshot, expectedFingerprint, gatesRecorded), snapshot, Failure: null);
    }

    /// <summary>Reads one class, turning anything the seam throws into an unreadable answer.</summary>
    /// <remarks>The seam is allowed to throw - <c>System.Management</c> throws for most of what can
    /// go wrong - and this runs before the window is shown, so nothing may come out of here.</remarks>
    private static WmiClassReading ReadOne(IWmiClassSource source, string className)
    {
        try
        {
            return source.Read(className) ?? WmiClassReading.Unreadable("the reader returned nothing");
        }
        catch (Exception ex)
        {
            return WmiClassReading.Unreadable($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
