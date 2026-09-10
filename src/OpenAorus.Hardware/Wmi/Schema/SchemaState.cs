namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>What is registered on this machine, and whose it is.</summary>
/// <remarks>
/// <para>
/// Seven values, and every one of them has exactly one right answer to the three questions the app
/// asks: may it install, may it remove, may it write. There is deliberately no value for "we could
/// not tell" - that is not a state of the machine, it is the absence of a reading, and it is
/// carried by <see cref="SchemaReport.Status"/> being null instead. Folding the two together is
/// how a repository that would not answer ends up being registered over.
/// </para>
/// <para>
/// TWO OF THE SEVEN ARE THE SAME REGISTRATION WITH THE GATES PASSED AGAINST IT.
/// <see cref="Ours"/> pairs with <see cref="OursGated"/> and <see cref="Foreign"/> with
/// <see cref="ForeignGated"/>, because ownership and proof are two different questions: ownership
/// governs install and removal, the gates govern writes, and neither answer decides the other.
/// </para>
/// </remarks>
public enum SchemaStatus
{
    /// <summary>No <c>GB_WMIACPI_*</c> class and no marker. The machine as the owner left it.</summary>
    Absent,

    /// <summary>
    /// All four classes and no marker, or all four with a marker over a mapping we did not
    /// install. Control Center, or a hand-run <c>mofcomp</c>. Registered, not proven.
    /// </summary>
    Foreign,

    /// <summary><see cref="Foreign"/>, and both hardware gates passed against this same mapping.</summary>
    /// <remarks>
    /// The state of a laptop with Control Center installed and working, once the owner has run the
    /// checks. It writes and it is never registered over or removed - which is not a contradiction,
    /// because the two are answers to different questions. Gate A reads the firmware and compares
    /// every answer against the known-good reading; Gate B proves the <c>Set</c> class resolves.
    /// Neither asks whose schema it is, and a schema that passes both is proven to work whoever
    /// installed it. Refusing writes here was strictly worse than the behaviour this feature
    /// replaced, which wrote to Gigabyte's schema with no verification at all.
    /// </remarks>
    ForeignGated,

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
/// TWO QUESTIONS, AND THEY ARE NOT THE SAME QUESTION. Ownership governs install and removal; the
/// gates govern writes. Conflating them is what made a working Control Center machine read-only.
/// </para>
/// <para>
/// <em>Ownership.</em> Never register over a working schema: two schemas over one GUID is a state
/// nobody has tested, and the state afterwards is one where neither the owner nor the app can say
/// which one won. And never remove someone else's: our remove MOF <c>#pragma deleteclass</c>es by
/// name, and the names are Gigabyte's, so pressing Remove on a Control Center machine would take
/// Control Center's own registration away and break software we did not install. So
/// <see cref="CanInstall"/> and <see cref="CanRemove"/> are both false for
/// <see cref="SchemaStatus.Foreign"/> and for <see cref="SchemaStatus.ForeignGated"/> - the gates
/// buy no permission over somebody else's registration, and never will.
/// </para>
/// <para>
/// <see cref="SchemaStatus.OursEmptied"/> is not an exception to the second half of that; it is a
/// case the evidence takes out from under it. Our marker recording our own fingerprint over classes
/// that declare nothing is more than <see cref="SchemaStatus.Foreign"/> ever carries, and it is not
/// a shape a machine running someone else's schema can be in. It permits Remove and only Remove -
/// the owner has to be able to undo what this app did.
/// </para>
/// <para>
/// <em>Writes.</em> Registering is not proving, and proving does not depend on having registered.
/// Gate A reads the firmware and checks every answer against the known-good reading; Gate B proves
/// the <c>Set</c> class resolves. Neither one asks whose schema it is. So
/// <see cref="SchemaStatus.Ours"/> unlocks nothing and <see cref="SchemaStatus.Foreign"/> unlocks
/// nothing, while <see cref="SchemaStatus.OursGated"/> and <see cref="SchemaStatus.ForeignGated"/> -
/// the same two registrations with both gates passed <em>against this fingerprint</em> - each let a
/// write through.
/// </para>
/// <para>
/// That is stricter than this app was before the feature existed, not looser: it then wrote to
/// Gigabyte's schema with no verification at all.
/// </para>
/// </remarks>
public static class SchemaState
{
    /// <summary>Decides which of the seven states a machine is in.</summary>
    /// <param name="live">What was found on the machine.</param>
    /// <param name="expectedFingerprint">The fingerprint our install file records, which is the
    /// mapping we would put there. It decides ownership and nothing else - the gates are earned
    /// against whatever the classes bind, which on a Control Center machine is not this.</param>
    /// <param name="gatesRecorded">Whether both hardware gates have been recorded as passed
    /// <em>against the mapping these classes bind today</em>. Its one caller is
    /// <see cref="OpenAorus.Hardware.Config.SchemaRecord.ProvenFor"/>, which is where that
    /// comparison is made; a record earned against a schema that has since moved arrives here as
    /// false.</param>
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

        // All four, and no evidence we put them there. Control Center's, and left alone either
        // way - but the gates are about the mapping and not about the owner, so a machine that has
        // passed them writes.
        if (!live.MarkerPresent) return SomebodyElses(live, gatesRecorded);

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
                : SomebodyElses(live, gatesRecorded);

        return gatesRecorded ? SchemaStatus.OursGated : SchemaStatus.Ours;
    }

    /// <summary>Which of the two not-ours states a machine is in.</summary>
    /// <remarks>
    /// The live fingerprint has to be there. <paramref name="gatesRecorded"/> is a comparison
    /// against it, so a machine nothing could fingerprint is one no record can have been earned
    /// against - and null matching nothing is the same answer given here that
    /// <see cref="Config.SchemaRecord.ProvenFor"/> gives, rather than a second opinion about it.
    /// </remarks>
    private static SchemaStatus SomebodyElses(SchemaSnapshot live, bool gatesRecorded) =>
        gatesRecorded && live.LiveFingerprint is not null
            ? SchemaStatus.ForeignGated
            : SchemaStatus.Foreign;

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
    /// seven.</exception>
    public static bool CanInstall(SchemaStatus status) => status switch
    {
        SchemaStatus.Absent => true,
        SchemaStatus.Partial => true, // After a remove, which the installer runs first.
        SchemaStatus.Foreign => false,

        // Passing the gates proves the mapping works. It says nothing about whose registration it
        // is, and two schemas over one GUID is no less untested for having been checked.
        SchemaStatus.ForeignGated => false,

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
    /// seven.</exception>
    public static bool CanRemove(SchemaStatus status) => status switch
    {
        SchemaStatus.Absent => false, // Nothing to take away.
        SchemaStatus.Partial => true, // The leftovers are ours: the marker is deleted last.
        SchemaStatus.Foreign => false, // Deleting by Gigabyte's names would break Gigabyte's software.

        // And no less so for having been checked. If anything more: this is the machine whose
        // Control Center the owner has just watched work.
        SchemaStatus.ForeignGated => false,

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
    /// <returns>True in exactly the two states where both gates have been passed against the
    /// mapping the classes bind now, whoever registered it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not one of the
    /// seven.</exception>
    public static bool WritesUnlocked(SchemaStatus status) => status switch
    {
        SchemaStatus.OursGated => true,

        // The one the app is normally installed onto. Gate A read every method off these classes
        // and matched the answers against the known-good reading, Gate B resolved the Set class,
        // and neither of those is a question about ownership. Refusing here switched fan and
        // battery control off on a working Control Center machine and offered no way back, since
        // CanInstall is rightly false too.
        SchemaStatus.ForeignGated => true,

        SchemaStatus.Absent => false,
        SchemaStatus.Partial => false,
        SchemaStatus.Foreign => false, // Registered is not proven, whoever registered it.
        SchemaStatus.Ours => false, // Registered is not proven. See the remarks on this class.
        SchemaStatus.OursEmptied => false, // The classes declare no methods. There is nothing to call.
        _ => throw Unknown(status),
    };

    /// <summary>Says what the machine is in, and what the app will and will not do about it.</summary>
    /// <param name="status">The state.</param>
    /// <returns>Owner-facing prose, in the voice of the model-status banner.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not one of the
    /// seven.</exception>
    /// <remarks>
    /// <para>
    /// Every one of these names a cause. What the owner sees today is "step 1/5 setCurrentFanStep
    /// failed: not found", one step into a sequence, which describes a symptom of a machine whose
    /// schema was uninstalled by somebody else's uninstaller.
    /// </para>
    /// <para>
    /// And every one of them names the way out where there is one. This is the same text the
    /// startup banner shows, so a state that said "OpenAorus will neither register over that nor
    /// remove it" and stopped there would be telling a Control Center owner that nothing can be
    /// done on the very machine where the checks are all that is wanted.
    /// </para>
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
            "not install. It can still check that registration, and fan and battery writes stay " +
            "disabled until the read check and the charge-limit round trip have both passed " +
            "against it. Settings runs them.",

        SchemaStatus.ForeignGated =>
            "The Gigabyte WMI classes on this machine were registered by something else - Control " +
            "Center, or a hand-compiled MOF - and both hardware checks passed against them, so fan " +
            "and battery controls are available. OpenAorus will still neither register over that " +
            "registration nor remove it, and the checks stop counting the moment it changes.",

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

    /// <summary>What a refused fan or battery write says.</summary>
    /// <param name="what">What is switched off, e.g. <c>fan control</c>.</param>
    /// <returns>Owner-facing prose naming the cause and where the fix is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="what"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The two controllers hold a <c>bool</c> and not a state, so this cannot name which of the
    /// five locked states a machine is in - <see cref="Explain"/> does that, in the window. What it
    /// must do is name a cause instead of a symptom. The message it replaces was "step 1/5
    /// setCurrentFanStep failed: not found", which said "method" when the whole class was missing,
    /// arrived one step into a five-step sequence, and left the app looking broken rather than
    /// unconfigured.
    /// </para>
    /// <para>
    /// It says the interface has not been proved, and does not say who registered it, because the
    /// five locked states include <see cref="SchemaStatus.Foreign"/> - a machine where the classes
    /// are present and are Control Center's. Telling that owner nothing is registered would send
    /// them looking for a fault that is not there, and telling them it is not <em>ours</em> would
    /// point at the one thing that is neither wrong nor going to change.
    /// </para>
    /// </remarks>
    public static string LockedRefusal(string what)
    {
        ArgumentNullException.ThrowIfNull(what);

        return "OpenAorus has not proved the Gigabyte WMI interface on this machine, so " +
               $"{what} is switched off. Settings says what state the registration is in and " +
               "offers whatever can be done about it.";
    }

    /// <summary>
    /// Thrown rather than defaulted, so that an eighth state has to be answered for at every one of
    /// these four sites instead of quietly reading as "no" at three of them and blank at the fourth.
    /// </summary>
    /// <remarks><see cref="SchemaStatus.ForeignGated"/> is what this is for. Adding it meant four
    /// separate answers, and a default arm would have silently given it the locked, unremovable,
    /// unregisterable "no" at three sites - which is the regression it exists to undo.</remarks>
    private static ArgumentOutOfRangeException Unknown(SchemaStatus status) =>
        new(nameof(status), status, "Not one of the seven schema states.");
}
