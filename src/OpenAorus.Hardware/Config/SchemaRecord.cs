namespace OpenAorus.Hardware.Config;

/// <summary>
/// What this app did to the WMI schema on this machine, and what it proved about it.
/// </summary>
/// <remarks>
/// <para>
/// The gates are a one-off, not a check on every launch: Gate B writes to the firmware, and a
/// feature that wrote on every start would be doing the one thing it is rationed to doing once.
/// So the result is written down - and a written-down proof is worth exactly as long as the thing
/// it was proved about stays the same.
/// </para>
/// <para>
/// <see cref="Fingerprint"/> is what makes that expiry possible. It records the schema the gates
/// were earned against, so a registration that changes underneath - Control Center installed
/// afterwards, someone running <c>mofcomp</c> by hand, a WMI repository that rebuilt itself -
/// stops matching, and <see cref="ProvenFor"/> stops saying yes. Every id the gates checked is an
/// id nobody has checked once the mapping moves.
/// </para>
/// <para>
/// THIS IS A FILE ANYONE CAN EDIT, and it is the file that unlocks writing to the firmware. So it
/// is repaired at load and never trusted, exactly as <see cref="HotkeySettings.Repair"/> and
/// <see cref="AppSettings.RepairFans"/> are, through the one choke point in
/// <see cref="SettingsStore.Load"/>. <see cref="Repair"/> only ever takes a pass away; there is no
/// state of this object it can put one into.
/// </para>
/// </remarks>
public sealed class SchemaRecord
{
    /// <summary>
    /// How far in the future a recorded time may sit before it is treated as impossible.
    /// </summary>
    /// <remarks>An hour, because the times here are local ones: a record written in the hour
    /// before the clocks go back reads as up to an hour ahead on a machine nothing has happened
    /// to. Anything beyond that is a clock that was moved or a value that was typed.</remarks>
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromHours(1);

    /// <summary>Whether this app installed the schema that is on this machine.</summary>
    /// <remarks>
    /// Ownership, and nothing about proof. It is not read by <see cref="ProvenFor"/> and it is not
    /// read by <see cref="Repair"/>, because the gates prove a mapping rather than a registration:
    /// the machines this app is most often installed on are the ones carrying Control Center's own
    /// schema, and a record that could not hold a pass over one of those left those machines
    /// read-only with no way back. What may be installed and what may be removed is decided from
    /// the machine itself - the marker class and the live fingerprint - never from here.
    /// </remarks>
    public bool Registered { get; set; }

    /// <summary>The binding fingerprint the gates were run against, or null if none was recorded.</summary>
    /// <remarks><see cref="Wmi.Schema.SchemaFingerprint.Of"/>, which is recomputable from the live
    /// classes without invoking a single firmware method.</remarks>
    public string? Fingerprint { get; set; }

    /// <summary>Whether both hardware gates passed against <see cref="Fingerprint"/>.</summary>
    /// <remarks>Never read on its own to decide a write - see <see cref="ProvenFor"/>. On its own
    /// it says a pass happened, not that it still applies.</remarks>
    public bool GatesPassed { get; set; }

    /// <summary>When the gates were run, in local time.</summary>
    public DateTime? When { get; set; }

    /// <summary>What the gates said, for the diagnostics dump and the settings page.</summary>
    /// <remarks>Prose, and evidence of nothing: it is a note about a run that happened, and it is
    /// never consulted by any decision. <see cref="Repair"/> leaves it alone even when it clears
    /// the pass, because what a past run reported is still the most useful thing to show an owner
    /// asking why writes are locked.</remarks>
    public string? GateSummary { get; set; }

    /// <summary>Whether this record unlocks writes on the schema a machine is carrying now.</summary>
    /// <param name="liveFingerprint">The fingerprint recomputed from the live classes, or null if
    /// there was no method-bearing class to compute one from.</param>
    /// <returns>True only for a pass earned against this exact mapping.</returns>
    /// <remarks>
    /// <para>
    /// Null never matches, which is the right answer rather than a lenient one: a registration
    /// nothing could fingerprint is not one any past pass can vouch for.
    /// </para>
    /// <para>
    /// <see cref="Registered"/> is deliberately not consulted. The fingerprint is the whole of the
    /// safety here - it says the classes bind today exactly what the gates were run against - and
    /// who compiled those classes changes nothing about that. Requiring ours was the same mistake
    /// as refusing writes on <c>SchemaStatus.Foreign</c>, restated in the settings file.
    /// </para>
    /// </remarks>
    public bool ProvenFor(string? liveFingerprint) =>
        GatesPassed &&
        Fingerprint is not null &&
        liveFingerprint is not null &&
        string.Equals(Fingerprint, liveFingerprint, StringComparison.Ordinal);

    /// <summary>
    /// Clears a claimed pass that this app could not have written, and reports whether it had to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same job and the same call site as <see cref="HotkeySettings.Repair"/>: one choke point
    /// in <see cref="SettingsStore.Load"/>, one notice, no second mechanism.
    /// </para>
    /// <para>
    /// Two ways a record can claim more than it earned, and both are a pass this app never wrote. A
    /// pass with no fingerprint could never be invalidated, because there would be nothing to
    /// compare the live schema against. A pass dated in the future, or dated not at all, was not
    /// stamped by a gate run. Each clears <see cref="GatesPassed"/> and nothing else: the
    /// surrounding record is left to say what it says, and the gates can simply be run again.
    /// </para>
    /// <para>
    /// A pass over a registration this app did not make is NOT one of them, and used to be. That is
    /// the ordinary state of a laptop with Control Center on it once the owner has run the checks,
    /// and clearing it here switched fan and battery control off on exactly those machines every
    /// time the settings file was loaded.
    /// </para>
    /// <para>
    /// It cannot grant a pass. Every branch here writes <c>false</c>, which is what stops a
    /// hand-edited settings file from being a way to unlock writes to the firmware.
    /// </para>
    /// </remarks>
    /// <returns>True if a claimed pass was cleared.</returns>
    public bool Repair()
    {
        if (!GatesPassed) return false;

        if (string.IsNullOrWhiteSpace(Fingerprint) ||
            When is not { } when || when > DateTime.Now + MaxFutureSkew)
        {
            GatesPassed = false;
            return true;
        }

        return false;
    }
}
