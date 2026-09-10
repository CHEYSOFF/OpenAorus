namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>What is registered on this machine, and whose it is.</summary>
/// <remarks>
/// Six values, and every one of them has exactly one right answer to the three questions the app
/// asks: may it install, may it remove, may it write. There is deliberately no value for "we could
/// not tell" - that is not a state of the machine, it is the absence of a reading, and it is
/// carried by <see cref="SchemaReport.Status"/> being null instead. Folding the two together is
/// how a repository that would not answer ends up being registered over.
/// </remarks>
public enum SchemaStatus
{
    /// <summary>No <c>GB_WMIACPI_*</c> class and no marker. The machine as the owner left it.</summary>
    Absent,

    /// <summary>
    /// All four classes and no marker, or all four with a marker over a mapping we did not
    /// install. Control Center, or a hand-run <c>mofcomp</c>.
    /// </summary>
    Foreign,

    /// <summary>
    /// Some classes but not all, or a marker with classes missing. A half-finished removal, or a
    /// repository that rebuilt itself.
    /// </summary>
    Partial,

    /// <summary>All four classes, our marker, and the mapping we installed. Registered, not proven.</summary>
    Ours,

    /// <summary><see cref="Ours"/>, and both hardware gates passed against this same mapping.</summary>
    OursGated,

    /// <summary>
    /// All four classes present, our marker beside them recording the schema we install, and every
    /// method-bearing class declaring nothing at all. A WMI repository that rebuilt itself over our
    /// own registration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <see cref="Foreign"/> with one more piece of evidence, and it exists only so that
    /// the owner can get out of it. The classes bind nothing, so nothing may be written through
    /// them and nothing may be registered over them - but the marker says the registration was
    /// ours, and Remove deletes exactly what a registration of ours created. Classifying this as
    /// <see cref="Foreign"/> left the owner with an app that would not act on a mess the app
    /// itself had left, and an elevated <c>mofcomp</c> prompt as the only way out.
    /// </para>
    /// <para>
    /// IT CANNOT FIRE ON A CONTROL CENTER MACHINE. Two independent facts have to hold: the marker
    /// class - which only our install file declares - records the fingerprint our install file
    /// writes, and both method-bearing classes declare no methods. Control Center's classes
    /// declare 143 of them.
    /// </para>
    /// </remarks>
    OursEmptied,
}

/// <summary>What one look at the machine found.</summary>
/// <param name="GetPresent">Whether <c>GB_WMIACPI_Get</c> resolves.</param>
/// <param name="SetPresent">Whether <c>GB_WMIACPI_Set</c> resolves.</param>
/// <param name="DataPresent">Whether <c>GB_WMIACPI_Data</c> resolves.</param>
/// <param name="EventPresent">Whether <c>GB_WMIACPI_Event</c> resolves.</param>
/// <param name="MarkerPresent">Whether <c>OpenAorus_SchemaMarker</c> resolves - the class only our
/// install file declares, and the only evidence that a registration was ours.</param>
/// <param name="MarkerFingerprint">The fingerprint our marker instance records, or null if there is
/// no marker, it carries none, or it could not be read. It is what the registration wrote down
/// about itself, as against <paramref name="LiveFingerprint"/>, which is what the classes bind
/// today. It decides nothing on its own - <see cref="SchemaStatus.OursEmptied"/> needs this to be
/// our schema <em>and</em> the live classes to declare nothing.</param>
/// <param name="LiveFingerprint">The binding fingerprint recomputed from the live classes, or null
/// if there was no method-bearing class to compute one from. Null never matches, which is the
/// right answer: a registration nothing could fingerprint is not one we can call ours.</param>
/// <remarks>
/// Presence here means the class name resolves, nothing more. A class that is present but declares
/// no methods is still present - it is a name <c>mofcomp</c> would collide with - and it is told
/// apart from an absent one by <see cref="LiveFingerprint"/>, which renders it as
/// <see cref="SchemaFingerprint.Missing"/> rather than dropping it.
/// </remarks>
public sealed record SchemaSnapshot(
    bool GetPresent,
    bool SetPresent,
    bool DataPresent,
    bool EventPresent,
    bool MarkerPresent,
    string? MarkerFingerprint,
    string? LiveFingerprint)
{
    /// <summary>Whether all four <c>GB_WMIACPI_*</c> classes resolve.</summary>
    public bool AllClassesPresent => GetPresent && SetPresent && DataPresent && EventPresent;

    /// <summary>Whether any of the four resolves.</summary>
    public bool AnyClassPresent => GetPresent || SetPresent || DataPresent || EventPresent;
}

/// <summary>The classes this feature touches, and the one list every caller enumerates.</summary>
/// <remarks>Kept in one place so that a class added to the MOF cannot be left out of the check
/// that decides whether the MOF may be compiled.</remarks>
public static class SchemaClasses
{
    /// <summary>
    /// The two classes that carry methods, which are the two the fingerprint is computed over.
    /// </summary>
    /// <remarks><see cref="SchemaFingerprint.Of"/> renders only classes declaring methods, so the
    /// live half of the comparison has to be gathered from exactly these and no others.</remarks>
    public static IReadOnlyList<string> MethodBearing { get; } = new[]
    {
        WmiSchemaParser.GetClass,
        WmiSchemaParser.SetClass,
    };

    /// <summary>Every class a registration creates: the four recovered ones and our marker.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        WmiSchemaParser.GetClass,
        WmiSchemaParser.SetClass,
        WmiSchemaParser.DataClass,
        WmiSchemaParser.EventClass,
        MofWriter.MarkerClass,
    };
}

/// <summary>
/// Turns a look at the machine into the one answer the buttons and the write path read.
/// </summary>
/// <remarks>
/// <para>
/// TWO RULES, NEITHER NEGOTIABLE.
/// </para>
/// <para>
/// <em>Never register over a working schema.</em> Two schemas over one GUID is a state nobody has
/// tested, and the state afterwards is one where neither the owner nor the app can say which one
/// won. So <see cref="CanInstall"/> is false for <see cref="SchemaStatus.Foreign"/>.
/// </para>
/// <para>
/// <em>Never remove someone else's.</em> Our remove MOF <c>#pragma deleteclass</c>es by name, and
/// the names are Gigabyte's. If Control Center is installed and the owner presses Remove, we would
/// take Control Center's own registration away and break software we did not install. So
/// <see cref="CanRemove"/> is false for <see cref="SchemaStatus.Foreign"/> too.
/// </para>
/// <para>
/// <see cref="SchemaStatus.OursEmptied"/> is not an exception to that second rule; it is a case the
/// evidence takes out from under it. Our marker recording our own fingerprint over classes that
/// declare nothing is more than <see cref="SchemaStatus.Foreign"/> ever carries, and it is not a
/// shape a machine running someone else's schema can be in. It permits Remove and only Remove -
/// the owner has to be able to undo what this app did.
/// </para>
/// <para>
/// And a third, which is the design's own: registering is not proving.
/// <see cref="SchemaStatus.Ours"/> unlocks nothing. Only <see cref="SchemaStatus.OursGated"/>,
/// which is the same registration with both hardware gates passed <em>against this fingerprint</em>,
/// lets a write through.
/// </para>
/// </remarks>
public static class SchemaState
{
    /// <summary>Decides which of the six states a machine is in.</summary>
    /// <param name="live">What was found on the machine.</param>
    /// <param name="expectedFingerprint">The fingerprint our install file records, which is the
    /// mapping we would put there and the mapping the gates were earned against.</param>
    /// <param name="gatesRecorded">Whether both hardware gates have been recorded as passed. It is
    /// only consulted once the registration has already been established as ours, so a stale
    /// record cannot unlock anything on its own.</param>
    /// <returns>The state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="live"/> or
    /// <paramref name="expectedFingerprint"/> is null.</exception>
    public static SchemaStatus Classify(SchemaSnapshot live, string expectedFingerprint, bool gatesRecorded)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);

        // The order below is the logic, which is why it is a sequence of guards and not a switch:
        // each line is only reachable because every line above it did not fire.

        // Nothing of ours and nothing of anyone else's.
        if (!live.AnyClassPresent && !live.MarkerPresent) return SchemaStatus.Absent;

        // Something is there but not all of it. Marker or no marker, this is a machine to clean
        // up before it is a machine to register - and it is never one to write through.
        if (!live.AllClassesPresent) return SchemaStatus.Partial;

        // All four, and no evidence we put them there.
        if (!live.MarkerPresent) return SchemaStatus.Foreign;

        // All four and our marker, over a mapping that is not the one we install. Someone
        // recompiled by hand, or an older version of us registered it. Either way the marker is
        // outvoted: the fingerprint is what says which firmware methods these names reach.
        //
        // With one exception, and it is the only place the marker's own record is read. If the
        // marker says our schema and the classes say nothing at all, this is our registration
        // after a repository rebuild - so it may be taken away, though still never written
        // through and never registered over.
        if (!string.Equals(live.LiveFingerprint, expectedFingerprint, StringComparison.Ordinal))
            return WasOursBeforeTheRebuild(live, expectedFingerprint)
                ? SchemaStatus.OursEmptied
                : SchemaStatus.Foreign;

        return gatesRecorded ? SchemaStatus.OursGated : SchemaStatus.Ours;
    }

    /// <summary>
    /// The live fingerprint a machine renders when every method-bearing class is present and
    /// declares nothing.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="SchemaFingerprint.OfLive"/> over the same class list the live half of
    /// every comparison is gathered from, so it is the value that reading such a machine actually
    /// produces rather than a second spelling of it. A class that was absent instead of empty
    /// would not be in that map at all and would render differently - which is the distinction
    /// <see cref="SchemaFingerprint.Missing"/> exists to keep, and the reason this can be stated
    /// as one string comparison.
    /// </remarks>
    private static readonly Lazy<string> s_emptiedFingerprint = new(() => SchemaFingerprint.OfLive(
        SchemaClasses.MethodBearing.ToDictionary(
            name => name,
            _ => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.Ordinal),
            StringComparer.Ordinal)));

    /// <summary>
    /// Whether this is our own registration with its methods gone, rather than a stranger's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two facts, both required, and together strictly more evidence than
    /// <see cref="SchemaStatus.Foreign"/> ever carries. The marker - a class only our install file
    /// declares - records the fingerprint that same file writes; and every method-bearing class is
    /// present and declares nothing.
    /// </para>
    /// <para>
    /// A Control Center machine fails both: it has no marker of ours, and its classes declare 143
    /// methods. A marker whose record could not be read comes through as null, which matches
    /// nothing - so an unreadable marker withholds this and never grants it.
    /// </para>
    /// </remarks>
    private static bool WasOursBeforeTheRebuild(SchemaSnapshot live, string expectedFingerprint) =>
        string.Equals(live.MarkerFingerprint, expectedFingerprint, StringComparison.Ordinal) &&
        string.Equals(live.LiveFingerprint, s_emptiedFingerprint.Value, StringComparison.Ordinal);

    /// <summary>Whether the install MOF may be compiled on a machine in this state.</summary>
    /// <param name="status">The state.</param>
    /// <returns>True for a machine with nothing on it, or one with leftovers to clear first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not one of the
    /// six.</exception>
    public static bool CanInstall(SchemaStatus status) => status switch
    {
        SchemaStatus.Absent => true,
        SchemaStatus.Partial => true, // After a remove, which the installer runs first.
        SchemaStatus.Foreign => false,
        SchemaStatus.Ours => false,
        SchemaStatus.OursGated => false,

        // Never. The class names are already taken, -class:createonly would refuse anyway, and
        // the whole of the extra permission this state carries is the one direction that undoes
        // something rather than creating it. Remove first, then this machine reads as Absent and
        // installs like any other.
        SchemaStatus.OursEmptied => false,

        _ => throw Unknown(status),
    };

    /// <summary>Whether the remove MOF may be compiled on a machine in this state.</summary>
    /// <param name="status">The state.</param>
    /// <returns>True only where every class the remove file names is one we are entitled to
    /// delete.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not one of the
    /// six.</exception>
    public static bool CanRemove(SchemaStatus status) => status switch
    {
        SchemaStatus.Absent => false, // Nothing to take away.
        SchemaStatus.Partial => true, // The leftovers are ours: the marker is deleted last.
        SchemaStatus.Foreign => false, // Deleting by Gigabyte's names would break Gigabyte's software.
        SchemaStatus.Ours => true,
        SchemaStatus.OursGated => true,

        // The one thing this state is for. Our marker records the schema our install file writes,
        // and the classes beside it declare nothing - which is what our own registration looks
        // like after a repository rebuild, and nothing else. Withholding Remove here leaves the
        // owner unable to undo something the app did.
        SchemaStatus.OursEmptied => true,

        _ => throw Unknown(status),
    };

    /// <summary>Whether fan and battery writes are allowed on a machine in this state.</summary>
    /// <param name="status">The state.</param>
    /// <returns>True in exactly one of the six states.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not one of the
    /// six.</exception>
    public static bool WritesUnlocked(SchemaStatus status) => status switch
    {
        SchemaStatus.OursGated => true,
        SchemaStatus.Absent => false,
        SchemaStatus.Partial => false,
        SchemaStatus.Foreign => false,
        SchemaStatus.Ours => false, // Registered is not proven. See the remarks on this class.
        SchemaStatus.OursEmptied => false, // The classes declare no methods. There is nothing to call.
        _ => throw Unknown(status),
    };

    /// <summary>Says what the machine is in, and what the app will and will not do about it.</summary>
    /// <param name="status">The state.</param>
    /// <returns>Owner-facing prose, in the voice of the model-status banner.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not one of the
    /// six.</exception>
    /// <remarks>
    /// Every one of these names a cause. What the owner sees today is "step 1/5 setCurrentFanStep
    /// failed: not found", one step into a sequence, which describes a symptom of a machine whose
    /// schema was uninstalled by somebody else's uninstaller.
    /// </remarks>
    public static string Explain(SchemaStatus status) => status switch
    {
        SchemaStatus.Absent =>
            "The Gigabyte WMI interface is not registered on this machine, so fan and battery " +
            "controls are unavailable. OpenAorus can register its own copy from Settings; it needs " +
            "administrator rights and it can be undone.",

        SchemaStatus.Foreign =>
            "The Gigabyte WMI classes are already registered by something else - Control Center, or " +
            "a hand-compiled MOF. OpenAorus will neither register over that nor remove it: the " +
            "classes carry Gigabyte's names, so removing them would break software OpenAorus did " +
            "not install.",

        SchemaStatus.Partial =>
            "Only part of the Gigabyte WMI registration is on this machine, which is what a " +
            "half-finished removal or a rebuilt WMI repository leaves behind. What is left is " +
            "OpenAorus's own; Settings can clear it and register again.",

        SchemaStatus.Ours =>
            "OpenAorus registered the Gigabyte WMI interface and the registration still matches " +
            "what it installed. Fan and battery writes stay disabled until the read check and the " +
            "charge-limit round trip have both passed.",

        SchemaStatus.OursGated =>
            "OpenAorus registered the Gigabyte WMI interface and both hardware checks passed " +
            "against it, so fan and battery controls are available. Settings can remove the " +
            "registration again at any time.",

        SchemaStatus.OursEmptied =>
            "The Gigabyte WMI classes OpenAorus registered are still on this machine but no longer " +
            "declare any methods, which is what a rebuilt WMI repository leaves behind. Fan and " +
            "battery controls stay disabled because those names now reach nothing. OpenAorus's own " +
            "marker records the schema it installed, so Settings can remove the registration and " +
            "then register it again.",

        _ => throw Unknown(status),
    };

    /// <summary>
    /// Thrown rather than defaulted, so that a seventh state has to be answered for at every one of
    /// these four sites instead of quietly reading as "no" at three of them and blank at the fourth.
    /// </summary>
    private static ArgumentOutOfRangeException Unknown(SchemaStatus status) =>
        new(nameof(status), status, "Not one of the six schema states.");
}
