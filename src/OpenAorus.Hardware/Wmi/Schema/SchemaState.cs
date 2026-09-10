namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>What is registered on this machine, and whose it is.</summary>
/// <remarks>
/// Five values, and every one of them has exactly one right answer to the three questions the app
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
}

/// <summary>What one look at the machine found.</summary>
/// <param name="GetPresent">Whether <c>GB_WMIACPI_Get</c> resolves.</param>
/// <param name="SetPresent">Whether <c>GB_WMIACPI_Set</c> resolves.</param>
/// <param name="DataPresent">Whether <c>GB_WMIACPI_Data</c> resolves.</param>
/// <param name="EventPresent">Whether <c>GB_WMIACPI_Event</c> resolves.</param>
/// <param name="MarkerPresent">Whether <c>OpenAorus_SchemaMarker</c> resolves - the class only our
/// install file declares, and the only evidence that a registration was ours.</param>
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
/// And a third, which is the design's own: registering is not proving.
/// <see cref="SchemaStatus.Ours"/> unlocks nothing. Only <see cref="SchemaStatus.OursGated"/>,
/// which is the same registration with both hardware gates passed <em>against this fingerprint</em>,
/// lets a write through.
/// </para>
/// </remarks>
public static class SchemaState
{
    /// <summary>Decides which of the five states a machine is in.</summary>
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
        if (!string.Equals(live.LiveFingerprint, expectedFingerprint, StringComparison.Ordinal))
            return SchemaStatus.Foreign;

        return gatesRecorded ? SchemaStatus.OursGated : SchemaStatus.Ours;
    }

    /// <summary>Whether the install MOF may be compiled on a machine in this state.</summary>
    /// <param name="status">The state.</param>
    /// <returns>True for a machine with nothing on it, or one with leftovers to clear first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not one of the
    /// five.</exception>
    public static bool CanInstall(SchemaStatus status) => status switch
    {
        SchemaStatus.Absent => true,
        SchemaStatus.Partial => true, // After a remove, which the installer runs first.
        SchemaStatus.Foreign => false,
        SchemaStatus.Ours => false,
        SchemaStatus.OursGated => false,
        _ => throw Unknown(status),
    };

    /// <summary>Whether the remove MOF may be compiled on a machine in this state.</summary>
    /// <param name="status">The state.</param>
    /// <returns>True only where every class the remove file names is one we are entitled to
    /// delete.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not one of the
    /// five.</exception>
    public static bool CanRemove(SchemaStatus status) => status switch
    {
        SchemaStatus.Absent => false, // Nothing to take away.
        SchemaStatus.Partial => true, // The leftovers are ours: the marker is deleted last.
        SchemaStatus.Foreign => false, // Deleting by Gigabyte's names would break Gigabyte's software.
        SchemaStatus.Ours => true,
        SchemaStatus.OursGated => true,
        _ => throw Unknown(status),
    };

    /// <summary>Whether fan and battery writes are allowed on a machine in this state.</summary>
    /// <param name="status">The state.</param>
    /// <returns>True in exactly one of the five states.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not one of the
    /// five.</exception>
    public static bool WritesUnlocked(SchemaStatus status) => status switch
    {
        SchemaStatus.OursGated => true,
        SchemaStatus.Absent => false,
        SchemaStatus.Partial => false,
        SchemaStatus.Foreign => false,
        SchemaStatus.Ours => false, // Registered is not proven. See the remarks on this class.
        _ => throw Unknown(status),
    };

    /// <summary>Says what the machine is in, and what the app will and will not do about it.</summary>
    /// <param name="status">The state.</param>
    /// <returns>Owner-facing prose, in the voice of the model-status banner.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not one of the
    /// five.</exception>
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

        _ => throw Unknown(status),
    };

    /// <summary>
    /// Thrown rather than defaulted, so that a sixth state has to be answered for at every one of
    /// these four sites instead of quietly reading as "no" at three of them and blank at the fourth.
    /// </summary>
    private static ArgumentOutOfRangeException Unknown(SchemaStatus status) =>
        new(nameof(status), status, "Not one of the five schema states.");
}
