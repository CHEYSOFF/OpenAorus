using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Both gates, run against a fake controller.
/// </summary>
/// <remarks>
/// Gate B is the one that matters here. Get and Set are different classes with different id
/// spaces - id 88 is CheckHeavyLoading in one and SetSuperQuiet in the other - so a perfect Gate A
/// says nothing at all about whether a fan write will land where it is aimed.
/// </remarks>
public class SchemaGateRunnerTests
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");

    private static FakeGigabyteWmi HealthyMachine()
    {
        var wmi = new FakeGigabyteWmi();
        foreach (var m in KnownGoodReadingTests.Reference().Methods)
        {
            if (!m.Answered) { wmi.FailOn.Add(m.Method); continue; }
            wmi.Responses[m.Method] = m.Values.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }
        return wmi;
    }

    [Fact]
    public void Reading_the_machine_produces_the_shape_gate_a_compares()
    {
        var readings = SchemaGateRunner.Read(HealthyMachine());

        Assert.NotEmpty(readings.Methods);
        Assert.Equal(15, readings.FanTable.Count);
    }

    [Fact]
    public void Gate_a_passes_on_a_machine_answering_exactly_what_the_known_good_dump_recorded()
    {
        var wmi = HealthyMachine();
        // The fan table has to be answered per index, which Respond cannot express.
        StubFanTable(wmi, KnownGoodReadingTests.Reference().FanTable);

        var v = SchemaGateRunner.RunGateA(wmi, KnownGoodReadingTests.Reference(), Kd);

        Assert.True(v.Passed, v.Summary());
    }

    [Fact]
    public void Gate_a_reads_and_never_writes()
    {
        // "Reads cannot damage anything, so this gate is free to fail loudly." It is only free to
        // if it really is all reads.
        var wmi = HealthyMachine();
        SchemaGateRunner.RunGateA(wmi, KnownGoodReadingTests.Reference(), Kd);

        Assert.All(wmi.Calls, c => Assert.Equal(WmiClass.Get, c.Class));
    }

    [Fact]
    public void Gate_b_writes_back_exactly_what_it_read()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", 97);

        var v = SchemaGateRunner.RunGateB(wmi);

        Assert.True(v.Passed, v.Summary());
        var write = Assert.Single(wmi.Calls, c => c.Class == WmiClass.Set);
        Assert.Equal("SetChargeStop", write.Method);
        Assert.Equal(97, write.Data);
    }

    [Fact]
    public void Gate_b_reads_back_afterwards_because_a_set_that_did_nothing_looks_the_same_as_one_that_worked()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", 80);

        SchemaGateRunner.RunGateB(wmi);

        Assert.Equal(2, wmi.Calls.Count(c => c.Method == "GetChargeStop"));
        Assert.True(wmi.Calls.FindLastIndex(c => c.Method == "GetChargeStop")
                    > wmi.Calls.FindIndex(c => c.Method == "SetChargeStop"));
    }

    [Fact]
    public void A_value_that_does_not_survive_the_round_trip_fails_the_gate()
    {
        // The Set class resolving somewhere else entirely.
        var wmi = new SequencedChargeStop(first: 80, second: 42);

        var v = SchemaGateRunner.RunGateB(wmi);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("80", StringComparison.Ordinal) && f.Contains("42", StringComparison.Ordinal));
    }

    [Fact]
    public void A_set_that_fails_outright_fails_the_gate_and_says_what_the_controller_said()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", 80);
        wmi.FailOn.Add("SetChargeStop");

        var v = SchemaGateRunner.RunGateB(wmi);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("SetChargeStop", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(59)]
    [InlineData(101)]
    [InlineData(4096)]
    public void A_charge_stop_it_would_not_be_safe_to_write_back_is_refused_rather_than_written(int value)
    {
        // THE REFUSAL THE DESIGN DOES NOT MENTION. "Write back what you read" is only harmless
        // while what you read is a charge percentage. Writing 0 back is a laptop that does not
        // charge, and 4096 truncates to 0 in the byte cast and does the same thing.
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", value);

        var v = SchemaGateRunner.RunGateB(wmi);

        Assert.False(v.Passed);
        Assert.DoesNotContain(wmi.Calls, c => c.Class == WmiClass.Set);
        // Inconclusive, not condemned: the registration may be fine and this reading merely odd.
        Assert.Contains(v.Warnings, w => w.Contains("could not be run", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_get_that_answers_without_a_value_is_refused_rather_than_written_to()
    {
        // The method resolved and came back empty, which is not a reading of anything. Reaching
        // for a fallback here would turn "no answer" into a number and write it.
        var wmi = new FakeGigabyteWmi();
        wmi.Responses["GetChargeStop"] = new Dictionary<string, object>();

        var v = SchemaGateRunner.RunGateB(wmi);

        Assert.False(v.Passed);
        Assert.DoesNotContain(wmi.Calls, c => c.Class == WmiClass.Set);
    }

    [Fact]
    public void The_band_it_refuses_outside_is_the_one_the_battery_controller_already_enforces()
    {
        // One definition, in the shape FanSafety keeps for the fan floors.
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", BatteryController.MinStop);
        Assert.True(SchemaGateRunner.RunGateB(wmi).Passed);

        wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", BatteryController.MaxStop);
        Assert.True(SchemaGateRunner.RunGateB(wmi).Passed);
    }

    [Fact]
    public void A_get_that_does_not_answer_at_all_never_reaches_the_write()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.FailOn.Add("GetChargeStop");

        var v = SchemaGateRunner.RunGateB(wmi);

        Assert.False(v.Passed);
        Assert.DoesNotContain(wmi.Calls, c => c.Class == WmiClass.Set);
    }

    [Fact]
    public void Gate_b_touches_nothing_but_the_charge_stop()
    {
        // No fan method, no policy method. The one write in this plan is the one write it makes.
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", 80);

        SchemaGateRunner.RunGateB(wmi);

        Assert.All(wmi.Calls, c => Assert.Equal("ChargeStop", c.Method[3..]));
    }

    [Fact]
    public void Gate_b_leaves_the_charge_limit_where_it_found_it_without_a_restoring_write()
    {
        // There is nothing to restore, and a restoring write would be a second write - which is
        // the thing this whole gate is rationed to one of.
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", 80);

        SchemaGateRunner.RunGateB(wmi);

        var write = Assert.Single(wmi.Calls, c => c.Class == WmiClass.Set);
        Assert.Equal(80, write.Data);
    }

    [Fact]
    public void A_verdict_says_which_gate_gave_it()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", 80);

        Assert.StartsWith("Gate B:", SchemaGateRunner.RunGateB(wmi).Summary(), StringComparison.Ordinal);
        Assert.StartsWith("Gate A:", SchemaGateRunner.RunGateA(HealthyMachine(), KnownGoodReadingTests.Reference(), Kd).Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_fails_gate_a_never_writes_anything_at_all()
    {
        // GATE B DOES NOT RUN AFTER A FAILED GATE A. Gate B's write is only defensible while the
        // read it echoes is believable, and a failed Gate A is the machine saying it is not.
        var wmi = new FakeGigabyteWmi();   // answers everything, including the 30 that must refuse

        var run = SchemaGateRunner.Run(wmi, KnownGoodReadingTests.Reference(), Kd);

        Assert.False(run.GateA.Passed);
        Assert.Null(run.GateB);
        Assert.False(run.Passed);
        Assert.DoesNotContain(wmi.Calls, c => c.Class == WmiClass.Set);
    }

    [Fact]
    public void A_run_of_both_gates_on_a_healthy_machine_passes_and_reports_both()
    {
        var wmi = HealthyMachine();
        StubFanTable(wmi, KnownGoodReadingTests.Reference().FanTable);

        var run = SchemaGateRunner.Run(wmi, KnownGoodReadingTests.Reference(), Kd);

        Assert.True(run.Passed, run.Summary());
        Assert.Contains("Gate A", run.Summary(), StringComparison.Ordinal);
        Assert.Contains("Gate B", run.Summary(), StringComparison.Ordinal);
        Assert.Equal(97, Assert.Single(wmi.Calls, c => c.Class == WmiClass.Set).Data);
    }

    [Fact]
    public void A_run_whose_gate_b_fails_is_not_a_pass()
    {
        var wmi = HealthyMachine();
        StubFanTable(wmi, KnownGoodReadingTests.Reference().FanTable);
        wmi.FailOn.Add("SetChargeStop");

        var run = SchemaGateRunner.Run(wmi, KnownGoodReadingTests.Reference(), Kd);

        Assert.True(run.GateA.Passed, run.GateA.Summary());
        Assert.False(run.Passed);
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaGateRunner.RunGateB(null!));
        Assert.Throws<ArgumentNullException>(() => SchemaGateRunner.Read(null!));
        Assert.Throws<ArgumentNullException>(() => SchemaGateRunner.RunGateA(null!, KnownGoodReadingTests.Reference(), Kd));
        Assert.Throws<ArgumentNullException>(() => SchemaGateRunner.Run(new FakeGigabyteWmi(), null!, Kd));
    }

    private static void StubFanTable(FakeGigabyteWmi wmi, IReadOnlyList<FanSlot> table) =>
        wmi.FanTable = table.ToDictionary(s => s.Index, s => (s.Temperature, s.Duty));

    /// <summary>A machine whose charge stop reads differently the second time.</summary>
    private sealed class SequencedChargeStop : IGigabyteWmi
    {
        private readonly int _first, _second;
        private int _reads;
        public SequencedChargeStop(int first, int second) { _first = first; _second = second; }

        public WmiResult Invoke(WmiClass cls, string method, IReadOnlyDictionary<string, object>? args = null) =>
            cls == WmiClass.Get && method == "GetChargeStop"
                ? WmiResult.Ok(new Dictionary<string, object> { ["Data"] = _reads++ == 0 ? _first : _second })
                : WmiResult.Ok();
    }
}
