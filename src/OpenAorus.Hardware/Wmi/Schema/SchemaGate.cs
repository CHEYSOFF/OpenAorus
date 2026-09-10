using System.Globalization;
using OpenAorus.Hardware.Profiles;

namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>What a gate decided, and everything it noticed on the way.</summary>
/// <param name="Passed">Whether the registration may go on to the next gate.</param>
/// <param name="Failures">Every rule that was broken, most informative first. Empty when
/// <paramref name="Passed"/>.</param>
/// <param name="Warnings">Things worth recording that are not evidence of a wrong mapping - a
/// handful of methods changing side, a fan reading zero on a cool machine.</param>
/// <param name="Compared">How many methods were actually judged. A thin comparison is a weak
/// verdict even when it passes, so the number travels with the answer rather than being
/// recomputed by whoever reads it.</param>
/// <param name="Gate">Which gate said this. It travels with the verdict rather than being supplied
/// at print time, because the two gates prove different things and a line that named the wrong one
/// would have the log claim a write was tested when only reads were.</param>
public sealed record GateVerdict(
    bool Passed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings,
    int Compared,
    string Gate = "Gate A")
{
    /// <summary>A verdict with nothing broken.</summary>
    /// <param name="compared">How many methods were judged.</param>
    /// <param name="warnings">Anything worth recording that is not a failure.</param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="warnings"/> is null.</exception>
    public static GateVerdict Pass(int compared, params string[] warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        return new GateVerdict(true, Array.Empty<string>(), warnings, compared);
    }

    /// <summary>One line for the log and the diagnostics dump.</summary>
    /// <returns>The verdict, how much evidence was behind it, and the first thing that broke.</returns>
    public string Summary() => Passed
        ? string.Format(CultureInfo.InvariantCulture, "{0}: passed, {1} methods compared, {2} warnings", Gate, Compared, Warnings.Count)
        : string.Format(CultureInfo.InvariantCulture, "{0}: FAILED, {1} compared - {2}", Gate, Compared,
            Failures.Count > 0 ? Failures[0] : "no reason recorded");
}

/// <summary>
/// Gate A: whether a fresh registration is reading the firmware, or reading something else.
/// </summary>
/// <remarks>
/// <para>
/// A MOF maps names to numbers. If a method id is wrong, the app calls a firmware method it does
/// not believe it is calling, on the controller that governs this machine's cooling and its
/// charging. Gate A is the cheap half of the defence against that: reads cannot damage anything,
/// so it is free to fail loudly and often.
/// </para>
/// <para>
/// GATE A PROVES NOTHING ABOUT WRITING. <c>GB_WMIACPI_Get</c> and <c>GB_WMIACPI_Set</c> are
/// separate classes with separate <c>WmiMethodId</c> spaces, so every id in this gate being right
/// says nothing whatsoever about any id in the other class. Passing here does not unlock a write,
/// and nothing downstream may treat it as though it did. Gate B - one <c>GetChargeStop</c> /
/// <c>SetChargeStop</c> / <c>GetChargeStop</c> round trip, whose write is a no-op by construction -
/// exists for exactly that reason.
/// </para>
/// <para>
/// This class never calls WMI, and it calls no <c>Set</c> method of any kind. It is handed two
/// <see cref="GateReadings"/> - one taken from the machine, one parsed from the checked-in
/// known-good dump - and returns a verdict, in the same way <see cref="Fans.FanSafety"/> decides
/// about a curve without knowing where the curve came from. The reads themselves are the
/// untestable part and live outside.
/// </para>
/// <para>
/// NOTHING HERE MAY THROW on a reading, however malformed. This runs to decide what the window
/// offers; a gate that threw would take the window with it. The only exceptions are the null
/// checks, which are programming errors and not readings.
/// </para>
/// <para>
/// Four checks, in descending order of how much they would catch:
/// </para>
/// <list type="number">
/// <item><description><em>The implemented/unimplemented partition.</em> The known-good dump
/// answers 42 of its 72 <c>Get</c> methods and refuses the other 30 with <c>Invalid object</c>.
/// The firmware decides which to refuse by method id, so a shifted block shows up here as methods
/// swapping sides - which is the one check that catches a permutation among methods whose values
/// all look equally plausible.</description></item>
/// <item><description><em>Plausible temperatures.</em> A wrong id reads a duty or a status byte
/// as a temperature.</description></item>
/// <item><description><em>The fan table, read to its terminator.</em> The three data ids behind
/// <c>GetFanIndexValue</c> are exercised by nothing else.</description></item>
/// <item><description><em><c>GetChargeStop</c> answering in range.</em> Gate B is about to write
/// through this method; finding out here that it is not there is cheaper.</description></item>
/// </list>
/// <para>
/// And three methods that are never used as evidence at all. <c>GetPEGorSG</c>,
/// <c>GetFanPWMStatus</c> and <c>GetFanAdjustStatus</c> are unimplemented on this model and answer
/// with whatever the previous call left in the buffer: all three returned 115 in the known-good
/// dump and all three returned 229 in a later one taken at duty 229. They cannot appear in the
/// partition either, because whether a stale buffer answers at all depends on what ran before it.
/// </para>
/// </remarks>
public static class SchemaGate
{
    /// <summary>Coldest reading accepted as a temperature, in °C.</summary>
    /// <remarks>Tighter than <see cref="Fans.FanSafety.MinPlausibleTemperature"/> on purpose. That
    /// one guards a running machine, where a genuinely cold reading has to be believed; this one
    /// judges a registration, and a laptop that has just answered a WMI call is not at 19 °C.</remarks>
    public const int MinTemperature = 20;

    /// <summary>Hottest reading accepted as a temperature, in °C.</summary>
    public const int MaxTemperature = 110;

    /// <summary>
    /// How much of the implemented/unimplemented partition must survive before the mapping is
    /// judged unchanged.
    /// </summary>
    /// <remarks>Not 1.0. Firmware revisions and machine state move a few of these, and a gate that
    /// demanded a perfect match would fail an owner whose BIOS is one version newer. A shifted
    /// block of ids moves far more than a tenth of them.</remarks>
    public const double MinPartitionAgreement = 0.90;

    /// <summary>The fewest fan-table slots that may precede the terminator.</summary>
    /// <remarks>Two, not fifteen. A machine running a custom curve reads back the curve, a
    /// <c>(0,0)</c> terminator and stale slots after it, and the shortest curve the app will write
    /// is short. What this rules out is <c>GetFanIndexValue</c> answering zeroes for everything,
    /// which is what an unbound method looks like.</remarks>
    public const int MinFanSlots = 2;

    /// <summary>The lowest charge limit <c>GetChargeStop</c> may report.</summary>
    public const int MinChargeStop = 0;

    /// <summary>The highest charge limit <c>GetChargeStop</c> may report.</summary>
    public const int MaxChargeStop = 100;

    /// <summary>Slowest fan speed treated as a running fan, in rpm.</summary>
    private const int MinPlausibleRpm = 200;

    /// <summary>Fastest fan speed treated as real, in rpm.</summary>
    private const int MaxPlausibleRpm = 12000;

    /// <summary>The out parameter almost every <c>Get</c> method answers with.</summary>
    private const string DataValue = "Data";

    /// <summary>
    /// The methods whose answers are whatever the last call left in the buffer, and which no check
    /// may read.
    /// </summary>
    /// <remarks>
    /// Named here rather than inline because every check has to skip the same three, and a check
    /// that forgot one would be reading the duty of the previous call and calling it evidence. See
    /// the second note at the foot of <c>docs/research/dump-aorus-17g-kd-known-good.txt</c>.
    /// </remarks>
    public static IReadOnlyList<string> StaleBufferMethods { get; } = new[]
    {
        "GetPEGorSG",
        "GetFanPWMStatus",
        "GetFanAdjustStatus",
    };

    /// <summary>Judges a reading taken from this machine against the one we know was correct.</summary>
    /// <param name="actual">What the machine answered through the fresh registration.</param>
    /// <param name="reference">The known-good reading, normally
    /// <see cref="KnownGoodReading.Parse"/> over the checked-in dump.</param>
    /// <param name="profile">The detected model, which carries the duty scale the fan table is
    /// read on and whether this model has a primary GPU temperature at all.</param>
    /// <returns>The verdict. Never throws on the content of a reading, however broken.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static GateVerdict CheckReads(GateReadings actual, GateReadings reference, ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(profile);

        var failures = new List<string>();
        var warnings = new List<string>();

        var answers = ByName(actual.Methods);

        // A1 first, because it is the only check that can catch a permutation among methods whose
        // values are all equally plausible, and because a reader who sees it fail has the whole
        // list of what moved.
        var compared = CheckPartition(answers, reference, failures, warnings);
        CheckTemperatures(answers, profile, failures);
        CheckFanTable(actual.FanTable, profile, failures);
        CheckChargeStop(answers, failures);
        CheckFanSpeeds(answers, profile, warnings);

        return failures.Count == 0
            ? GateVerdict.Pass(compared, warnings.ToArray())
            : new GateVerdict(false, failures, warnings, compared);
    }

    /// <summary>
    /// A1 - whether the same methods the firmware refused before are the ones it refuses now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The strongest signal in the known-good dump, and the reason its <c>Invalid object</c> lines
    /// are kept rather than trimmed: they are the firmware saying "I do not implement that method
    /// id". A registration that made all 30 succeed would be more suspicious than one that
    /// reproduced them.
    /// </para>
    /// <para>
    /// Compared only over methods present in both readings. The known-good was rendered by a 0.2.0
    /// build whose method list is not necessarily today's, and comparing against a name the other
    /// side never asked about would count a missing question as a wrong answer.
    /// </para>
    /// </remarks>
    /// <returns>How many methods were judged.</returns>
    private static int CheckPartition(
        IReadOnlyDictionary<string, MethodReading> answers,
        GateReadings reference,
        List<string> failures,
        List<string> warnings)
    {
        var compared = 0;
        var moved = new List<string>();

        foreach (var expected in reference.Methods)
        {
            if (IsStaleBuffer(expected.Method)) continue;
            if (!answers.TryGetValue(expected.Method, out var got)) continue;

            compared++;
            if (got.Answered != expected.Answered) moved.Add(expected.Method);
        }

        if (compared == 0)
        {
            failures.Add(
                "No method was read on both sides, so there is nothing to compare the registration " +
                "against. This is a reading that did not happen, not a machine that answered badly.");
            return 0;
        }

        var agreement = (compared - moved.Count) / (double)compared;
        if (agreement < MinPartitionAgreement)
        {
            failures.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Only {0:P0} of the {1} methods compared implement the same way they did in the " +
                "known-good reading, below the {2:P0} this gate requires. A method that used to " +
                "answer and now refuses - or the reverse - is the firmware being asked a different " +
                "method id. These changed side: {3}.",
                agreement, compared, MinPartitionAgreement, string.Join(", ", moved)));
        }
        else if (moved.Count > 0)
        {
            warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} of {1} methods implement differently than in the known-good reading, which is " +
                "within tolerance for a different firmware revision: {2}.",
                moved.Count, compared, string.Join(", ", moved)));
        }

        return compared;
    }

    /// <summary>A2 - whether the temperature methods answered with temperatures.</summary>
    /// <remarks>A wrong id here reads a duty or a status byte and calls it °C, which is how a
    /// machine ends up being told it is cool while it cooks. <c>getGpuTemp2</c> is deliberately
    /// not checked: it read 0 in the known-good dump, so on this model it is the dead fallback
    /// behind a primary source that works.</remarks>
    private static void CheckTemperatures(
        IReadOnlyDictionary<string, MethodReading> answers,
        ModelProfile profile,
        List<string> failures)
    {
        CheckTemperature(answers, "getCpuTemp", failures);
        if (profile.HasGpuTemp1) CheckTemperature(answers, "getGpuTemp1", failures);
    }

    private static void CheckTemperature(
        IReadOnlyDictionary<string, MethodReading> answers,
        string method,
        List<string> failures)
    {
        if (ReadData(answers, method) is not { } celsius)
        {
            failures.Add($"{method} did not answer. This model reads its temperature through it, so a registration that cannot is not one to write through.");
            return;
        }

        if (celsius < MinTemperature || celsius > MaxTemperature)
        {
            failures.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} answered {1}, which is not a temperature a running laptop reports. Expected {2}..{3} °C. " +
                "A duty or a status byte read as °C looks exactly like this.",
                method, celsius, MinTemperature, MaxTemperature));
        }
    }

    /// <summary>A3 - the fan table, read from slot 0 to its terminator and no further.</summary>
    /// <remarks>
    /// <para>
    /// The design says "fifteen slots, monotonic in temperature", and that is true only of the
    /// firmware's own default table. A machine with a custom curve applied reads back the curve, a
    /// <c>(0,0)</c> terminator, and stale values in the slots after it - so a gate demanding
    /// fifteen ordered slots would fail every machine whose only fault is that its owner uses a
    /// curve.
    /// </para>
    /// <para>
    /// The prefix is what <c>GetFanIndexValue</c>'s three data ids actually produced, so it is the
    /// only part worth judging. A slot that would not read at all is absent from the list and ends
    /// the prefix there, for the same reason: past it, nothing was measured.
    /// </para>
    /// <para>
    /// Three rules over that prefix - it is long enough, it switches at temperatures, and it is
    /// ordered and inside the duty maximum - and the middle one is not redundant. Two of the three
    /// data ids being each other's leaves the other two rules satisfied.
    /// </para>
    /// </remarks>
    private static void CheckFanTable(IReadOnlyList<FanSlot> table, ModelProfile profile, List<string> failures)
    {
        var points = new List<FanSlot>();
        for (var i = 0; i < table.Count; i++)
        {
            var slot = table[i];
            if (slot.Index != i) break;                       // a slot that would not read
            if (slot.Temperature == 0 && slot.Duty == 0) break;  // the terminator
            points.Add(slot);
        }

        if (points.Count < MinFanSlots)
        {
            failures.Add(string.Format(
                CultureInfo.InvariantCulture,
                "The fan table holds {0} slot(s) before its terminator, fewer than the {1} required. " +
                "GetFanIndexValue answering zeroes for every slot is what an unbound method looks like.",
                points.Count, MinFanSlots));
            return;
        }

        foreach (var point in points)
        {
            // Zero is legitimate here where MinTemperature would not be - the firmware's own
            // table starts its first slot at 0 °C - so only the ceiling is worth stating. What it
            // catches is the one permutation every other rule in this check is blind to: swap
            // GetFanIndexValue's two data ids and the table stays ordered and stays inside the
            // duty maximum, because the columns become each other's. It just starts switching at
            // 229 °C. Nothing legitimate reaches here: the firmware's table tops out at 89 and
            // FanCurve refuses to write a point above 100.
            if (point.Temperature < 0 || point.Temperature > MaxTemperature)
            {
                failures.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "The fan table switches at {0} °C in slot {1}, which is not a temperature. Expected 0..{2} °C. " +
                    "GetFanIndexValue's temperature and duty read through separate data ids, and this is what having them the wrong way round looks like.",
                    point.Temperature, point.Index, MaxTemperature));
                break;
            }
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].Temperature < points[i - 1].Temperature)
            {
                failures.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "The fan table is not ordered: slot {0} switches at {1} °C, below slot {2}'s {3} °C. " +
                    "The controller reads this table in order, so a table out of order was not read as written.",
                    points[i].Index, points[i].Temperature, points[i - 1].Index, points[i - 1].Temperature));
                break;
            }
        }

        foreach (var point in points)
        {
            if (point.Duty > profile.DutyMax)
            {
                failures.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "The fan table holds duty {0} at slot {1}, above this model's maximum of {2}. " +
                    "Either the table is being read through the wrong data id or the duty scale is not this controller's.",
                    point.Duty, point.Index, profile.DutyMax));
                break;
            }
        }
    }

    /// <summary>A4 - <c>GetChargeStop</c> answering something a charge limit could be.</summary>
    /// <remarks>A range and not the 97 the known-good recorded: the owner can change the charge
    /// limit, and pinning the value would fail a machine that is perfectly fine. Gate B writes
    /// through this method, so a method that is not there is worth catching before the write.</remarks>
    private static void CheckChargeStop(IReadOnlyDictionary<string, MethodReading> answers, List<string> failures)
    {
        if (ReadData(answers, "GetChargeStop") is not { } stop)
        {
            failures.Add("GetChargeStop did not answer. Gate B round-trips a write through it, so there is no point going on to a gate that cannot read its own result back.");
            return;
        }

        if (stop < MinChargeStop || stop > MaxChargeStop)
        {
            failures.Add(string.Format(
                CultureInfo.InvariantCulture,
                "GetChargeStop answered {0}, which is not a charge limit. Expected {1}..{2}.",
                stop, MinChargeStop, MaxChargeStop));
        }
    }

    /// <summary>The fan speeds, which are recorded and never allowed to fail the gate.</summary>
    /// <remarks>
    /// The known-good raw values byte-swap into plausible speeds - 44046 into 3756 rpm, 33295 into
    /// 3970 - and a later reading at full duty gave 6934 into 5659 and 21527 into 5972, each
    /// consistent with its duty. That is real evidence, but it is not evidence that can be
    /// required: a cool machine sitting on a desk legitimately reads zero, so a rule strict enough
    /// to catch a wrong id here would fail a machine doing nothing wrong.
    /// </remarks>
    private static void CheckFanSpeeds(
        IReadOnlyDictionary<string, MethodReading> answers,
        ModelProfile profile,
        List<string> warnings)
    {
        var fans = Math.Clamp(profile.FanCount, 1, 2);   // getRpm1 and getRpm2 are all the schema recovered
        for (var fan = 1; fan <= fans; fan++)
        {
            var method = "getRpm" + fan.ToString(CultureInfo.InvariantCulture);
            if (ReadData(answers, method) is not { } raw)
            {
                warnings.Add($"{method} did not answer, so fan {fan} could not be corroborated.");
                continue;
            }

            var rpm = profile.RpmByteSwapped ? ByteSwap(raw) : raw;
            if (rpm < MinPlausibleRpm || rpm > MaxPlausibleRpm)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} answered {1}, which reads as {2} rpm - outside {3}..{4}. Expected on an idle machine; " +
                    "on a loaded one it would suggest the wrong method id.",
                    method, raw, rpm, MinPlausibleRpm, MaxPlausibleRpm));
            }
        }
    }

    /// <summary>The controller reports rpm with its bytes the other way round.</summary>
    private static int ByteSwap(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    /// <summary>The <c>Data</c> a method answered, or null if it did not answer one.</summary>
    private static int? ReadData(IReadOnlyDictionary<string, MethodReading> answers, string method) =>
        answers.TryGetValue(method, out var reading) && reading.Answered &&
        reading.Values.TryGetValue(DataValue, out var value)
            ? value
            : null;

    private static bool IsStaleBuffer(string method) =>
        StaleBufferMethods.Contains(method, StringComparer.Ordinal);

    /// <summary>Indexes a reading by method name, keeping the first answer for a repeated name.</summary>
    private static IReadOnlyDictionary<string, MethodReading> ByName(IReadOnlyList<MethodReading> methods)
    {
        var byName = new Dictionary<string, MethodReading>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            if (!byName.ContainsKey(method.Method)) byName[method.Method] = method;
        }
        return byName;
    }
}
