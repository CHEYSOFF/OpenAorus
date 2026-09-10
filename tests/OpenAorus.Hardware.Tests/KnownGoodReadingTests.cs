using System.IO;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The last reading taken while the schema still worked, read back off disk.
/// </summary>
public class KnownGoodReadingTests
{
    internal static string Path =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "schema", "dump-aorus-17g-kd-known-good.txt");

    internal static GateReadings Reference() => KnownGoodReading.Parse(File.ReadAllText(Path));

    [Fact]
    public void It_reads_every_method_line_and_keeps_which_ones_answered()
    {
        var r = Reference();

        Assert.Equal(72, r.Methods.Count);
        Assert.Equal(30, r.Methods.Count(m => !m.Answered));
        Assert.Equal(42, r.Methods.Count(m => m.Answered));
    }

    [Fact]
    public void An_invalid_object_answer_is_recorded_as_a_method_this_model_does_not_implement()
    {
        // Not a failure. It is part of the shape Gate A compares against, and reproducing it is
        // evidence the ids are right.
        var m = Reference().Methods.Single(x => x.Method == "GetBatteryCount");

        Assert.False(m.Answered);
        Assert.Empty(m.Values);
    }

    [Fact]
    public void A_single_valued_answer_keeps_its_number()
    {
        Assert.Equal(115, Reference().Methods.Single(m => m.Method == "GetCPUFanDuty").Values["Data"]);
        Assert.Equal(90, Reference().Methods.Single(m => m.Method == "getCpuTemp").Values["Data"]);
        Assert.Equal(53, Reference().Methods.Single(m => m.Method == "getGpuTemp1").Values["Data"]);
    }

    [Fact]
    public void A_multi_valued_answer_keeps_all_of_them()
    {
        var m = Reference().Methods.Single(x => x.Method == "GetPowerOnTime");

        Assert.Equal(37, m.Values["Year"]);
        Assert.Equal(9, m.Values["Month"]);
        Assert.Equal(23, m.Values["Day"]);
    }

    [Fact]
    public void The_anchor_comments_are_stripped_rather_than_parsed_as_part_of_the_value()
    {
        // "GetChargeStop: Data=97      # ANCHOR - Gate B round-trips this"
        Assert.Equal(97, Reference().Methods.Single(m => m.Method == "GetChargeStop").Values["Data"]);
    }

    [Fact]
    public void The_fan_table_comes_back_as_fifteen_slots()
    {
        var t = Reference().FanTable;

        Assert.Equal(15, t.Count);
        Assert.Equal(new FanSlot(0, 0, 57), t[0]);
        Assert.Equal(new FanSlot(14, 89, 229), t[14]);
    }

    [Fact]
    public void The_known_good_table_is_the_firmwares_own_and_tops_out_at_the_duty_maximum()
    {
        var t = Reference().FanTable;

        Assert.Equal(229, t.Max(s => s.Duty));
        Assert.True(t.Zip(t.Skip(1)).All(p => p.Second.Temperature >= p.First.Temperature));
    }

    [Fact]
    public void Comment_lines_and_the_header_are_not_mistaken_for_readings()
    {
        Assert.DoesNotContain(Reference().Methods, m => m.Method.StartsWith("#", StringComparison.Ordinal));
        Assert.DoesNotContain(Reference().Methods, m => m.Method.Contains("Model", StringComparison.Ordinal));
    }

    [Fact]
    public void A_text_with_no_readings_in_it_is_a_hard_failure()
    {
        Assert.Throws<FormatException>(() => KnownGoodReading.Parse("nothing here"));
        Assert.Throws<ArgumentNullException>(() => KnownGoodReading.Parse(null!));
    }
}
