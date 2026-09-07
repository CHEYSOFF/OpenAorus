using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The logging half of the OS boundary, pulled above the seam so it can be tested at all.
/// </summary>
/// <remarks>
/// <para>
/// Why this class exists is a failure mode, not a feature. If the byte-indexing assumption in
/// <see cref="RawInputDecoder"/> is inverted, every real report is rejected outright and the
/// bench sees nothing happen - which is indistinguishable from "this chassis emits no such
/// reports", an open question in the research. A report that arrives and decodes to nothing has
/// to be observable somewhere, or VERIFY 8.2 cannot tell those two failures apart.
/// </para>
/// <para>
/// It is also the flood control. Both recorders run on callbacks that fire regardless of focus,
/// so "write a line per event" has to be bounded before it is written, not after.
/// </para>
/// </remarks>
public class HotkeyTraceTests
{
    [Fact]
    public void An_untouched_trace_says_so_rather_than_rendering_nothing()
    {
        // An empty section still has to appear in the dump: "no reports arrived" is the single
        // most informative line on the page when a Fn key does nothing.
        var text = new HotkeyTrace().Render();

        Assert.Contains("reports=0", text);
        Assert.Contains("unreadable-packets=0", text);
        Assert.Contains("events=0", text);
        Assert.Contains("faults=0", text);
        Assert.Contains("nothing has arrived", text);
    }

    [Fact]
    public void A_report_is_listed_by_its_bytes_whatever_it_decodes_to()
    {
        // 4, 1, 12 is not a documented pattern, so the decoder yields nothing for it. The bytes
        // still have to reach the dump, because their presence is what says the channel works.
        var trace = new HotkeyTrace();
        trace.RecordReport(new byte[] { 4, 1, 12, 38 });

        var text = trace.Render();
        Assert.Equal(1, trace.ReportCount);
        Assert.Contains("04 01 0C 26", text);
        Assert.Contains("4 bytes", text);
    }

    [Fact]
    public void A_packet_that_yields_no_report_is_counted_apart_from_one_that_does()
    {
        // The two failures this separates: "WM_INPUT never arrives" (nothing at all recorded)
        // against "WM_INPUT arrives and the walk rejects it" (the x64 header assumption wrong).
        var trace = new HotkeyTrace();
        trace.RecordUnreadablePacket(36);

        var text = trace.Render();
        Assert.Equal(0, trace.ReportCount);
        Assert.Equal(1, trace.UnreadablePacketCount);
        Assert.Contains("unreadable", text);
        Assert.Contains("36", text);
    }

    [Fact]
    public void A_packet_of_unknown_length_says_so_rather_than_claiming_zero_bytes()
    {
        var trace = new HotkeyTrace();
        trace.RecordUnreadablePacket(0);

        Assert.Contains("size unknown", trace.Render());
    }

    [Fact]
    public void An_oversized_packet_length_is_reported_without_wrapping()
    {
        // The length comes from the OS as a uint; the recorder takes a long so a value past
        // int.MaxValue reaches the dump as itself rather than as a negative number.
        var trace = new HotkeyTrace();
        trace.RecordUnreadablePacket(5_000_000_000L);

        Assert.Contains("5000000000", trace.Render());
    }

    [Fact]
    public void An_event_is_listed_by_the_data_value_it_carried()
    {
        var trace = new HotkeyTrace();
        trace.RecordEvent(202);

        Assert.Equal(1, trace.EventCount);
        Assert.Contains("Data=202", trace.Render());
    }

    [Fact]
    public void An_event_with_no_readable_data_lists_the_properties_that_did_arrive()
    {
        // The property name is unconfirmed on hardware. If the value lives under another name,
        // this line is what says so - otherwise a wrong name is a subscription that runs and
        // reports nothing, forever, with nothing on screen to say why.
        var trace = new HotkeyTrace();
        trace.RecordUnreadableEvent(new[] { "Brightness", "TIME_CREATED" });

        var text = trace.Render();
        Assert.Equal(1, trace.UnreadableEventCount);
        Assert.Contains("Brightness", text);
        Assert.Contains("TIME_CREATED", text);
    }

    [Fact]
    public void An_event_carrying_no_properties_at_all_still_renders()
    {
        var trace = new HotkeyTrace();
        trace.RecordUnreadableEvent(Array.Empty<string>());

        Assert.Contains("no properties", trace.Render());
    }

    [Fact]
    public void Repeated_faults_at_one_site_are_counted_but_listed_once()
    {
        // THE FLOOD RULE. A subscriber that throws on every report throws on a callback that
        // fires regardless of focus; one line per message would bury the dump and one banner per
        // message would bury the owner.
        var trace = new HotkeyTrace();
        for (var i = 0; i < 500; i++) trace.RecordFault("WM_INPUT", new InvalidOperationException("boom"));

        var text = trace.Render();
        Assert.Equal(500, trace.FaultCount);
        Assert.Equal(1, CountOccurrences(text, "InvalidOperationException"));
        Assert.Contains("faults=500", text);
    }

    [Fact]
    public void A_packet_shape_that_keeps_arriving_is_listed_once_and_counted()
    {
        // The failure this has to survive is the interesting one: if the header assumption is
        // wrong, EVERY packet is unreadable, and one line each would push the reports - the
        // actual evidence - out of the ring within a few keypresses.
        var trace = new HotkeyTrace();
        for (var i = 0; i < 200; i++) trace.RecordUnreadablePacket(36);
        trace.RecordReport(new byte[] { 4, 0, 0, 137 });

        var text = trace.Render();
        Assert.Equal(200, trace.UnreadablePacketCount);
        Assert.Equal(1, CountOccurrences(text, "36 bytes"));
        Assert.Contains("04 00 00 89", text);   // the report survived the flood
    }

    [Fact]
    public void Packets_of_different_lengths_are_listed_separately()
    {
        var trace = new HotkeyTrace();
        trace.RecordUnreadablePacket(36);
        trace.RecordUnreadablePacket(40);

        var text = trace.Render();
        Assert.Contains("36 bytes", text);
        Assert.Contains("40 bytes", text);
    }

    [Fact]
    public void An_event_shape_that_keeps_arriving_is_listed_once_and_counted()
    {
        // Brightness events arrive on this class too, one per step of a ramp, and none of them
        // carries a Data value. They must not be able to bury the ones that do.
        var trace = new HotkeyTrace();
        for (var i = 0; i < 200; i++) trace.RecordUnreadableEvent(new[] { "Brightness" });
        trace.RecordUnreadableEvent(new[] { "Something", "Else" });

        var text = trace.Render();
        Assert.Equal(201, trace.UnreadableEventCount);
        Assert.Equal(1, CountOccurrences(text, "Brightness"));
        Assert.Contains("Something, Else", text);
    }

    [Fact]
    public void Faults_at_different_sites_are_listed_separately()
    {
        // Deduplication is per site, not global: "the registration failed" and "a subscriber
        // threw" are different diagnoses and losing either to the other would be worse than
        // the flood.
        var trace = new HotkeyTrace();
        trace.RecordFault("WM_INPUT", new InvalidOperationException("boom"));
        trace.RecordFault("raw-input registration", "Win32 error 5");

        var text = trace.Render();
        Assert.Contains("WM_INPUT", text);
        Assert.Contains("raw-input registration", text);
        Assert.Contains("Win32 error 5", text);
    }

    [Fact]
    public void A_fault_detail_spanning_lines_is_flattened_onto_one()
    {
        // An exception message with newlines in it would otherwise forge entries in a dump that
        // is read line by line.
        var trace = new HotkeyTrace();
        trace.RecordFault("WM_INPUT", new InvalidOperationException("first\r\nsecond\nthird"));

        var line = Assert.Single(trace.Render().Split('\n'), l => l.Contains("first"));
        Assert.Contains("second", line);
        Assert.Contains("third", line);
    }

    [Fact]
    public void The_ring_keeps_the_most_recent_entries_and_says_how_many_it_dropped()
    {
        var trace = new HotkeyTrace();
        for (var i = 0; i < HotkeyTrace.Capacity + 8; i++) trace.RecordReport(new[] { (byte)i });

        var text = trace.Render();
        Assert.Contains("8 earlier", text);
        Assert.Contains($"#{HotkeyTrace.Capacity + 8} report", text);   // the newest survives
        Assert.DoesNotContain("#1 report", text);                       // the oldest is gone
    }

    [Fact]
    public void The_counts_are_not_bounded_by_the_ring()
    {
        // The lines are capped; the counts are the evidence that the channel is alive at all,
        // and a bench session is longer than 32 keypresses.
        var trace = new HotkeyTrace();
        for (var i = 0; i < 1000; i++) trace.RecordReport(new byte[] { 4, 0, 0, 137 });

        Assert.Equal(1000, trace.ReportCount);
        Assert.Contains("reports=1000", trace.Render());
    }

    [Fact]
    public void The_ring_is_the_only_thing_that_grows_without_bound_checking()
    {
        // Every recorder shares one ring, so a flood on any one of them cannot starve the dump
        // of room for the others beyond Capacity lines in total.
        var trace = new HotkeyTrace();
        for (var i = 0; i < 100; i++)
        {
            trace.RecordReport(new byte[] { 4, 0, 0, 137 });
            trace.RecordEvent(202);
            trace.RecordUnreadablePacket(36);
        }

        var lines = trace.Render().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length <= HotkeyTrace.Capacity + 3, $"{lines.Length} lines");
    }

    [Fact]
    public void A_null_report_is_a_programming_error_and_says_so()
    {
        // Same line RawInputBuffer and RawInputDecoder draw: no device can send a null array,
        // so it can only be this app calling itself wrongly. The WM_INPUT boundary catches it.
        Assert.Throws<ArgumentNullException>(() => new HotkeyTrace().RecordReport(null!));
    }

    [Fact]
    public void A_null_property_list_is_a_programming_error_too()
    {
        Assert.Throws<ArgumentNullException>(() => new HotkeyTrace().RecordUnreadableEvent(null!));
    }

    [Fact]
    public void A_null_fault_is_a_programming_error_too()
    {
        Assert.Throws<ArgumentNullException>(() => new HotkeyTrace().RecordFault("WM_INPUT", (Exception)null!));
        Assert.Throws<ArgumentNullException>(() => new HotkeyTrace().RecordFault(null!, "detail"));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
