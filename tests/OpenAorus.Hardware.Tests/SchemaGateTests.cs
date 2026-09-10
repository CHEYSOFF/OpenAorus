using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Gate A: is this registration reading the firmware, or reading something else?
/// </summary>
/// <remarks>
/// <para>
/// The strongest test in this file is the first one: the reading we know was correct is fed
/// straight back in and must pass. A gate that rejected the one dump taken while the schema
/// demonstrably worked would be useless, and a gate nobody had shown accepting anything would be
/// worse - it would lock the owner out with no way to tell a bad registration from a bad gate.
/// </para>
/// <para>
/// The second strongest is the one after it: a reading with two methods' values swapped, which is
/// what a shifted method id looks like from up here, must fail. Together they say the gate has
/// both a floor and teeth.
/// </para>
/// </remarks>
public class SchemaGateTests
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");
    private static GateReadings Reference() => KnownGoodReadingTests.Reference();

    [Fact]
    public void The_reading_we_know_was_correct_passes()
    {
        var v = SchemaGate.CheckReads(Reference(), Reference(), Kd);

        Assert.True(v.Passed, v.Summary());
        Assert.Empty(v.Failures);
        Assert.True(v.Compared > 60);
    }

    [Fact]
    public void A_machine_where_every_method_suddenly_answers_is_more_suspicious_than_one_that_reproduces_the_failures()
    {
        // Straight out of the research file's own notes. The 30 "Invalid object" answers are the
        // firmware saying "I do not implement that method id", so reproducing them is evidence.
        var reference = Reference();
        var everythingWorks = reference with
        {
            Methods = reference.Methods
                .Select(m => m.Answered ? m : m with { Answered = true, Values = new Dictionary<string, int> { ["Data"] = 1 } })
                .ToList(),
        };

        var v = SchemaGate.CheckReads(everythingWorks, reference, Kd);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("GetBatteryCount", StringComparison.Ordinal));
    }

    [Fact]
    public void A_block_of_shifted_method_ids_fails()
    {
        // What a wrong WmiMethodId actually looks like from up here: methods that used to answer
        // stop, and methods that used to fail start.
        var reference = Reference();
        var shifted = reference with
        {
            Methods = reference.Methods.Select(m => m with { Answered = !m.Answered }).ToList(),
        };

        var v = SchemaGate.CheckReads(shifted, reference, Kd);

        Assert.False(v.Passed);
    }

    [Fact]
    public void A_handful_of_methods_changing_side_is_tolerated()
    {
        // Firmware revisions and machine state move a few of these. The gate is looking for a
        // shifted block, not for a perfect match.
        var reference = Reference();
        var moved = reference.Methods.Take(3).Select(m => m with { Answered = !m.Answered });
        var actual = reference with { Methods = moved.Concat(reference.Methods.Skip(3)).ToList() };

        var v = SchemaGate.CheckReads(actual, reference, Kd);

        Assert.True(v.Passed, v.Summary());
        Assert.NotEmpty(v.Warnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(19)]
    [InlineData(111)]
    public void A_cpu_temperature_that_is_not_a_temperature_fails(int value)
    {
        var v = SchemaGate.CheckReads(WithValue(Reference(), "getCpuTemp", value), Reference(), Kd);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("getCpuTemp", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gpu_temperature_is_only_checked_on_a_model_that_has_one()
    {
        var noGpu = Kd with { HasGpuTemp1 = false };

        Assert.True(SchemaGate.CheckReads(WithValue(Reference(), "getGpuTemp1", 0), Reference(), noGpu).Passed);
        Assert.False(SchemaGate.CheckReads(WithValue(Reference(), "getGpuTemp1", 0), Reference(), Kd).Passed);
    }

    [Fact]
    public void The_fan_table_is_read_to_its_terminator_and_not_beyond()
    {
        // THE CORRECTION THE RESEARCH FORCES. A machine with a custom curve applied reads back
        // the curve, a (0,0) terminator, and stale values in the slots after it. Demanding
        // fifteen monotonic slots would fail every machine whose owner uses a curve.
        var custom = new List<FanSlot>
        {
            new(0, 40, 57), new(1, 50, 69), new(2, 60, 92), new(3, 70, 126),
            new(4, 80, 172), new(5, 90, 229), new(6, 0, 0),
        };
        for (var i = 7; i < 15; i++) custom.Add(new FanSlot(i, 86, 206));   // stale, and not in order

        var v = SchemaGate.CheckReads(Reference() with { FanTable = custom }, Reference(), Kd);

        Assert.True(v.Passed, v.Summary());
    }

    [Fact]
    public void A_fan_table_that_is_not_ordered_before_its_terminator_fails()
    {
        var scrambled = new List<FanSlot> { new(0, 80, 200), new(1, 40, 57), new(2, 0, 0) };

        var v = SchemaGate.CheckReads(Reference() with { FanTable = scrambled }, Reference(), Kd);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("fan table", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_fan_table_that_terminates_immediately_fails()
    {
        // GetFanIndexValue answering zeroes for everything is what an unbound method looks like.
        var empty = Enumerable.Range(0, 15).Select(i => new FanSlot(i, 0, 0)).ToList();

        var v = SchemaGate.CheckReads(Reference() with { FanTable = empty }, Reference(), Kd);

        Assert.False(v.Passed);
    }

    [Fact]
    public void A_fan_table_holding_a_duty_above_the_models_maximum_fails()
    {
        var tooHot = new List<FanSlot> { new(0, 40, 57), new(1, 90, 255), new(2, 0, 0) };

        var v = SchemaGate.CheckReads(Reference() with { FanTable = tooHot }, Reference(), Kd);

        Assert.False(v.Passed);
    }

    [Fact]
    public void A_fan_table_whose_two_data_ids_are_the_wrong_way_round_fails()
    {
        // The permutation A3 exists for, and the one every other rule in it misses. Swap
        // GetFanIndexValue's temperature and duty data ids and the known-good table is still
        // non-decreasing and still inside the duty maximum - the two columns are simply each
        // other's. What gives it away is that the table now claims to switch at 229 °C.
        var swapped = Reference().FanTable.Select(s => new FanSlot(s.Index, s.Duty, s.Temperature)).ToList();

        var v = SchemaGate.CheckReads(Reference() with { FanTable = swapped }, Reference(), Kd);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("fan table", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_duty_scale_that_is_not_this_controllers_fails()
    {
        // The 17G KD's own table runs on a scale of 229, and the known-good reading holds points
        // well above 100. Judged against a profile whose scale is 100 - which is what reading
        // this table through the wrong duty scale amounts to - most of it is out of range.
        var wrongScale = Kd with { DutyMax = 100 };

        var v = SchemaGate.CheckReads(Reference(), Reference(), wrongScale);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("fan table", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("GetPEGorSG")]
    [InlineData("GetFanPWMStatus")]
    [InlineData("GetFanAdjustStatus")]
    public void The_three_methods_that_return_stale_buffer_contents_are_never_used_as_evidence(string method)
    {
        // The research is explicit: all three returned 115 in the known-good dump and all three
        // returned 229 in a later one where the duty was 229. They are unimplemented, and their
        // value is whatever the last call left behind.
        Assert.Contains(method, SchemaGate.StaleBufferMethods);

        var reference = Reference();
        var flipped = reference with
        {
            Methods = reference.Methods
                .Select(m => m.Method == method ? m with { Answered = !m.Answered, Values = new Dictionary<string, int>() } : m)
                .ToList(),
        };

        Assert.True(SchemaGate.CheckReads(flipped, reference, Kd).Passed);
    }

    [Fact]
    public void A_reading_where_only_the_stale_buffer_methods_still_look_right_fails()
    {
        // The trap this gate is sized against: the three methods whose answers track the last
        // call's buffer keep looking exactly as they did in the known-good dump while everything
        // that is actually bound to firmware has stopped answering. Nothing about those three may
        // be able to carry a verdict.
        var reference = Reference();
        var onlyLies = reference with
        {
            Methods = reference.Methods
                .Select(m => SchemaGate.StaleBufferMethods.Contains(m.Method, StringComparer.Ordinal)
                    ? m
                    : m with { Answered = false, Values = new Dictionary<string, int>() })
                .ToList(),
            FanTable = Array.Empty<FanSlot>(),
        };

        var v = SchemaGate.CheckReads(onlyLies, reference, Kd);

        Assert.False(v.Passed);
        Assert.DoesNotContain(v.Failures, f => SchemaGate.StaleBufferMethods.Any(s => f.Contains(s, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(100)]
    public void The_charge_stop_is_checked_for_range_and_not_pinned_to_the_value_it_had(int value)
    {
        // The owner can change the charge limit. Pinning 97 would fail a machine that is fine.
        Assert.True(SchemaGate.CheckReads(WithValue(Reference(), "GetChargeStop", value), Reference(), Kd).Passed);
    }

    [Fact]
    public void A_charge_stop_outside_zero_to_a_hundred_fails()
    {
        Assert.False(SchemaGate.CheckReads(WithValue(Reference(), "GetChargeStop", 4096), Reference(), Kd).Passed);
    }

    [Fact]
    public void A_charge_stop_that_does_not_answer_at_all_fails_here_rather_than_in_gate_b()
    {
        // Gate B writes to this method. Finding out it is not there before writing is the point.
        var reference = Reference();
        var gone = reference with
        {
            Methods = reference.Methods
                .Select(m => m.Method == "GetChargeStop" ? m with { Answered = false, Values = new Dictionary<string, int>() } : m)
                .ToList(),
        };

        Assert.False(SchemaGate.CheckReads(gone, reference, Kd).Passed);
    }

    [Fact]
    public void An_rpm_that_byte_swaps_to_nonsense_is_a_warning_and_not_a_failure()
    {
        // A cool machine sitting on a desk legitimately reads zero, so a rule strict enough to
        // catch a wrong id here would fail a machine doing nothing wrong.
        var v = SchemaGate.CheckReads(WithValue(Reference(), "getRpm1", 0), Reference(), Kd);

        Assert.True(v.Passed);
        Assert.NotEmpty(v.Warnings);
    }

    [Fact]
    public void A_reading_where_nothing_answered_fails_loudly()
    {
        // The machine the owner has right now, if the registration did not take.
        var reference = Reference();
        var nothing = reference with
        {
            Methods = reference.Methods.Select(m => m with { Answered = false, Values = new Dictionary<string, int>() }).ToList(),
            FanTable = Array.Empty<FanSlot>(),
        };

        var v = SchemaGate.CheckReads(nothing, reference, Kd);

        Assert.False(v.Passed);
        Assert.True(v.Failures.Count >= 3);
    }

    [Fact]
    public void The_verdict_says_what_it_compared_so_a_thin_comparison_is_visible()
    {
        var reference = Reference();
        var few = reference with { Methods = reference.Methods.Take(4).ToList() };

        var v = SchemaGate.CheckReads(few, reference, Kd);

        Assert.Equal(4, v.Compared);
        Assert.Contains("4", v.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_passing_verdict_still_says_how_thin_the_evidence_was_and_what_it_noticed()
    {
        var v = GateVerdict.Pass(69, "getRpm2 did not answer");

        Assert.True(v.Passed);
        Assert.Empty(v.Failures);
        Assert.Equal("Gate A: passed, 69 methods compared, 1 warnings", v.Summary());
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaGate.CheckReads(null!, Reference(), Kd));
        Assert.Throws<ArgumentNullException>(() => SchemaGate.CheckReads(Reference(), null!, Kd));
        Assert.Throws<ArgumentNullException>(() => SchemaGate.CheckReads(Reference(), Reference(), null!));
    }

    private static GateReadings WithValue(GateReadings r, string method, int value) => r with
    {
        Methods = r.Methods
            .Select(m => m.Method == method
                ? m with { Answered = true, Values = new Dictionary<string, int> { ["Data"] = value } }
                : m)
            .ToList(),
    };
}
