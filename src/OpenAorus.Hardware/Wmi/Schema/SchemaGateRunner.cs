using System.Globalization;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Diagnostics;
using OpenAorus.Hardware.Profiles;

namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>What both gates said about one registration.</summary>
/// <param name="GateA">The read check. Always run.</param>
/// <param name="GateB">The round trip, or null if it was not run at all - which is what a failed
/// Gate A means. Null is not a pass and not a failure: it is the write never having been
/// attempted, and it is told apart from a Gate B that ran and refused, which is a verdict.</param>
public sealed record GateRun(GateVerdict GateA, GateVerdict? GateB)
{
    /// <summary>Whether this registration earned the right to be written through.</summary>
    /// <remarks>Both gates, both passed. A Gate B that never ran is not a pass, so a caller that
    /// only checked <c>GateA.Passed</c> could not accidentally unlock a write.</remarks>
    public bool Passed => GateA.Passed && GateB is { Passed: true };

    /// <summary>One line for the log, the record and the diagnostics dump.</summary>
    /// <returns>Both verdicts, or Gate A's alone with a note that Gate B never ran.</returns>
    public string Summary() => GateB is null
        ? GateA.Summary() + "; Gate B: not run - it never runs after a failed Gate A"
        : GateA.Summary() + "; " + GateB.Summary();
}

/// <summary>
/// Runs the two gates against a live controller: Gate A's reads, and Gate B's one round trip.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SchemaGate"/> decides; this asks the machine. The split is the same one
/// <see cref="Fans.FanSafety"/> keeps - the judgement is pure and tested over a fake, and the
/// part that touches hardware is thin enough to read.
/// </para>
/// <para>
/// WHY THERE IS A GATE B AT ALL, since it is the easiest thing in this feature to talk oneself out
/// of. <c>GB_WMIACPI_Get</c> and <c>GB_WMIACPI_Set</c> are separate classes with separate
/// <c>WmiMethodId</c> spaces. Id 88 is <c>CheckHeavyLoading</c> in one and <c>SetSuperQuiet</c> in
/// the other. Gate A can therefore pass in full with every id in the <c>Set</c> class wrong, and
/// the first thing that would happen next is a fan write landing somewhere nobody aimed it. Gate B
/// is the only evidence in this feature that the <c>Set</c> class resolves to the firmware and that
/// at least one of its ids maps as recorded.
/// </para>
/// <para>
/// WHY <c>SetChargeStop</c> AND NOTHING ELSE. It is the only write in the recovered schema that is
/// a no-op by construction - writing back the value just read changes nothing, whatever the charge
/// policy is - and it cannot heat the machine. Every fan write either changes cooling or latches a
/// mode.
/// </para>
/// <para>
/// AND WHY ECHOING A VALUE IS ONLY HARMLESS ONCE IT HAS BEEN RANGE-CHECKED. THIS IS THE WHOLE
/// SAFETY ARGUMENT AND IT MUST NOT BE WEAKENED BACK. "Write back what you read" is harmless only
/// if the read was right. Gate A cannot prove it was: <c>GetChargeStop</c> and <c>GetMaxCharge</c>
/// both answer a legal charge percentage, so a registration that has those two ids the wrong way
/// round passes Gate A untouched. Echo that reading and the value written is not the charge limit
/// at all - and 0 is a legal answer for several methods and an illegal thing to set, because a
/// charge limit of zero is a laptop that does not charge. A large reading is no better: the
/// parameter is a <c>uint8</c>, so 4096 truncates to 0 and does the same thing. So the value is
/// checked against <see cref="BatteryController.MinStop"/>..<see cref="BatteryController.MaxStop"/>
/// - the only band this app ever writes as a charge limit - BEFORE anything is written, and a
/// reading outside it ends the gate with no write at all. A value out there means either the read
/// method is not the one we think it is or the machine is in a state we do not understand, and
/// neither is a reason to touch the firmware.
/// </para>
/// <para>
/// That refusal is reported as a failed gate carrying a warning that the test <em>could not be
/// run</em>, rather than as proof of a bad registration: the mapping might be perfect and the
/// battery reading merely odd. Writes stay locked either way, which is the safe reading of both.
/// </para>
/// <para>
/// GATE B DOES NOT RUN AFTER A FAILED GATE A - see <see cref="Run"/>. Gate B's write is defensible
/// only while the read it echoes is believable, and a failed Gate A is the machine saying it is
/// not.
/// </para>
/// <para>
/// NOTHING IS RESTORED AFTERWARDS, and that is deliberate rather than an omission. The write is
/// the value that was already there, so there is nothing to put back; and a restoring write would
/// itself be a second write to a firmware method this feature has not yet earned the right to
/// write to. One write, or none.
/// </para>
/// </remarks>
public static class SchemaGateRunner
{
    /// <summary>The method Gate B reads and reads back.</summary>
    public const string RoundTripGet = "GetChargeStop";

    /// <summary>The one method this feature writes before writes are unlocked.</summary>
    public const string RoundTripSet = "SetChargeStop";

    /// <summary>How many fan-table slots the controller holds.</summary>
    public const int FanTableSlots = 15;

    /// <summary>The method the fan table is read through, one slot per call.</summary>
    private const string FanTableMethod = "GetFanIndexValue";

    /// <summary>The out parameter almost every <c>Get</c> method answers with.</summary>
    private const string DataValue = "Data";

    /// <summary>The BIOS's own spelling of the fan slot's temperature, kept as the schema has it.</summary>
    private const string SlotTemperature = "Temperture";

    /// <summary>The fan slot's duty, on the controller's scale.</summary>
    private const string SlotDuty = "Value";

    private const string GateBName = "Gate B";

    /// <summary>Said whenever Gate B ends without having proved or disproved anything.</summary>
    private const string Inconclusive =
        "This test could not be run, so nothing was proved either way about the Set class. " +
        "Writes stay locked.";

    /// <summary>Reads everything Gate A judges: every <c>Get</c> method, then the fan table.</summary>
    /// <param name="wmi">The controller to read through.</param>
    /// <returns>The reading, in the shape <see cref="SchemaGate.CheckReads"/> compares.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="wmi"/> is null.</exception>
    /// <remarks>
    /// The method list is <see cref="DiagnosticsDump.GetMethods"/> and not a second copy of it, so
    /// the gate and the dump an owner exports can never disagree about what was asked - which is
    /// the whole basis for diffing a failing machine's dump against the checked-in known-good one.
    /// A slot that would not read is left out rather than recorded as zeroes, because a zero that
    /// was never measured reads as the table's terminator.
    /// </remarks>
    public static GateReadings Read(IGigabyteWmi wmi)
    {
        ArgumentNullException.ThrowIfNull(wmi);

        var methods = new List<MethodReading>(DiagnosticsDump.GetMethods.Length);
        foreach (var method in DiagnosticsDump.GetMethods)
        {
            var result = wmi.Get(method);
            methods.Add(result.Success
                ? new MethodReading(method, Answered: true, Numbers(result))
                : new MethodReading(method, Answered: false, NoValues));
        }

        var table = new List<FanSlot>(FanTableSlots);
        for (var index = 0; index < FanTableSlots; index++)
        {
            var result = wmi.Invoke(WmiClass.Get, FanTableMethod,
                new Dictionary<string, object> { ["Index"] = (byte)index });
            if (!result.Success) continue;

            table.Add(new FanSlot(index, result.GetInt(SlotTemperature), result.GetInt(SlotDuty)));
        }

        return new GateReadings(methods, table);
    }

    /// <summary>Gate A: reads this machine and judges it against the known-good reading.</summary>
    /// <param name="wmi">The controller to read through.</param>
    /// <param name="reference">The known-good reading to compare against.</param>
    /// <param name="profile">The detected model.</param>
    /// <returns>The verdict. Reads only; this calls no <c>Set</c> method of any kind.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static GateVerdict RunGateA(IGigabyteWmi wmi, GateReadings reference, ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(profile);

        return SchemaGate.CheckReads(Read(wmi), reference, profile);
    }

    /// <summary>
    /// Gate B: read the charge stop, write the same value back, read it back again.
    /// </summary>
    /// <param name="wmi">The controller to round-trip through.</param>
    /// <returns>The verdict. Passing is the only evidence this feature has that the <c>Set</c>
    /// class reaches the firmware.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="wmi"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Only ever called after Gate A has passed - see <see cref="Run"/>, which is the sequencing
    /// and not a suggestion.
    /// </para>
    /// <para>
    /// The band check in front of the write is the load-bearing line in this method. See the
    /// remarks on this class before touching it: without it, a registration with
    /// <c>GetChargeStop</c> and <c>GetMaxCharge</c> transposed - which Gate A passes - would have
    /// this method write 0 into whatever <c>SetChargeStop</c>'s id actually reaches.
    /// </para>
    /// <para>
    /// The read-back has to be checked for both things it can be wrong about. It must equal what
    /// was written, which is what proves the <c>Set</c> landed in the slot the <c>Get</c> reads -
    /// a <c>Set</c> that silently did nothing looks identical to a correct no-op otherwise - and it
    /// must itself still be a charge limit, so that a machine whose charge stop is now some value
    /// this app would never write cannot be reported as a healthy round trip.
    /// </para>
    /// <para>
    /// Nothing here throws on anything the controller answers. This decides what the window offers.
    /// </para>
    /// </remarks>
    public static GateVerdict RunGateB(IGigabyteWmi wmi)
    {
        ArgumentNullException.ThrowIfNull(wmi);

        var failures = new List<string>();
        var warnings = new List<string>();

        var before = wmi.Get(RoundTripGet);
        if (!before.Success || ReadData(before) is not { } stop)
        {
            failures.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} did not answer a value ({1}), so there was nothing to write back and nothing was written.",
                RoundTripGet, before.Success ? "the call returned no Data" : before.Error ?? "no reason reported"));
            warnings.Add(Inconclusive);
            return Refused(failures, warnings);
        }

        // THE REFUSAL. Everything above this line is a read; everything below writes to the
        // firmware. A value outside the band this app writes means the reading is not one we can
        // echo, and the gate ends here having touched nothing.
        if (stop < BatteryController.MinStop || stop > BatteryController.MaxStop)
        {
            failures.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} answered {1}, outside the {2}..{3} % this app ever writes as a charge limit, so nothing was written. " +
                "Writing that back would not be the no-op this gate depends on being: a limit of 0 is a machine that does not " +
                "charge, and a value above 255 truncates in the uint8 the method takes - 4096 arrives as 0. Either {0} is not " +
                "the method we think it is, or this machine's charge limit is in a state this app did not set.",
                RoundTripGet, stop, BatteryController.MinStop, BatteryController.MaxStop));
            warnings.Add(Inconclusive);
            return Refused(failures, warnings);
        }

        // Safe by construction: the band checked above is 60..100, well inside a byte. The cast is
        // BatteryController's own, so the value written is the value that method would write.
        var write = wmi.SetData(RoundTripSet, (byte)stop);
        if (!write.Success)
        {
            failures.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} refused the write: {1}. The Set class does not reach the firmware, or not on the id this schema records.",
                RoundTripSet, write.Error ?? "no reason reported"));
            return Refused(failures, warnings);
        }

        var after = wmi.Get(RoundTripGet);
        if (!after.Success || ReadData(after) is not { } readBack)
        {
            failures.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} was written and then would not read back ({1}), so what the write did is unknown.",
                RoundTripSet, after.Success ? "the call returned no Data" : after.Error ?? "no reason reported"));
            warnings.Add(Inconclusive);
            return Refused(failures, warnings);
        }

        if (readBack != stop)
        {
            failures.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} read {1} before the write and {2} after it, when the value written was the value read. " +
                "The write did not land in the slot {0} reads, which is the Set class resolving somewhere else.",
                RoundTripGet, stop, readBack));
        }

        // Checked on its own account rather than inferred from the line above, because the two
        // rules answer different questions and the next reader may relax one of them.
        if (readBack < BatteryController.MinStop || readBack > BatteryController.MaxStop)
        {
            failures.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} reads {1} after the round trip, outside the {2}..{3} % this app writes. The charge limit is not somewhere this app would leave it.",
                RoundTripGet, readBack, BatteryController.MinStop, BatteryController.MaxStop));
        }

        return failures.Count == 0
            ? new GateVerdict(true, Array.Empty<string>(), warnings, Compared: 1, GateBName)
            : new GateVerdict(false, failures, warnings, Compared: 1, GateBName);
    }

    /// <summary>Runs both gates in the one order they may be run in.</summary>
    /// <param name="wmi">The controller.</param>
    /// <param name="reference">The known-good reading Gate A compares against.</param>
    /// <param name="profile">The detected model.</param>
    /// <returns>Gate A's verdict, and Gate B's if it was run.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// GATE B DOES NOT RUN IF GATE A DID NOT PASS, and this is the method that says so. Gate B's
    /// defence is that it echoes a value it has just read and range-checked; a failed Gate A is
    /// this machine's reads being untrustworthy, which is the one condition under which that
    /// defence does not hold. A failed Gate A therefore ends the run with no write attempted - the
    /// registration is refused on the reads alone.
    /// </remarks>
    public static GateRun Run(IGigabyteWmi wmi, GateReadings reference, ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(profile);

        var gateA = RunGateA(wmi, reference, profile);
        return gateA.Passed ? new GateRun(gateA, RunGateB(wmi)) : new GateRun(gateA, GateB: null);
    }

    /// <summary>A Gate B that ended without a completed round trip behind it.</summary>
    private static GateVerdict Refused(List<string> failures, List<string> warnings) =>
        new(false, failures, warnings, Compared: 0, GateBName);

    /// <summary>The <c>Data</c> a call answered, or null if it answered none or answered a non-number.</summary>
    /// <remarks>Null rather than a fallback on purpose. A fallback here would turn "the method said
    /// nothing" into a number, and that number is what would get written.</remarks>
    private static int? ReadData(WmiResult result)
    {
        if (!result.Out.TryGetValue(DataValue, out var value) || value is null) return null;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch (Exception) { return null; }   // A string, an array, an overflow - all "no reading".
    }

    /// <summary>Every out parameter that is a number, by name.</summary>
    /// <remarks>Anything that will not convert is dropped rather than defaulted to zero: Gate A
    /// reads these as evidence, and a zero nobody measured is not evidence.</remarks>
    private static IReadOnlyDictionary<string, int> Numbers(WmiResult result)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in result.Out)
        {
            if (pair.Value is null) continue;
            try { values[pair.Key] = Convert.ToInt32(pair.Value, CultureInfo.InvariantCulture); }
            catch (Exception) { /* Not a number, so not a reading. */ }
        }
        return values;
    }

    private static readonly IReadOnlyDictionary<string, int> NoValues =
        new Dictionary<string, int>(StringComparer.Ordinal);
}
