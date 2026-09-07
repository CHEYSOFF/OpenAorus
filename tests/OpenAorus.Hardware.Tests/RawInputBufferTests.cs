using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The walk over a <c>RAWINPUT</c> buffer, driven by hand-built buffers rather than by a keyboard.
/// </summary>
/// <remarks>
/// This is the whole of <c>WM_INPUT</c> handling that does not need a desktop. What is left after
/// it - the registration call, the message-only window and the extended styles - cannot be
/// exercised here and is in VERIFY instead.
/// </remarks>
public class RawInputBufferTests
{
    /// <summary>A buffer in the x64 layout: 24-byte header, then dwSizeHid, dwCount, the reports.</summary>
    private static byte[] Build(uint type, uint sizeHid, uint count, params byte[] payload)
    {
        var buffer = new byte[RawInputBuffer.HeaderSize + 8 + payload.Length];
        BitConverter.GetBytes(type).CopyTo(buffer, 0);
        BitConverter.GetBytes(sizeHid).CopyTo(buffer, RawInputBuffer.HeaderSize);
        BitConverter.GetBytes(count).CopyTo(buffer, RawInputBuffer.HeaderSize + 4);
        payload.CopyTo(buffer, RawInputBuffer.HeaderSize + 8);
        return buffer;
    }

    [Fact]
    public void One_four_byte_report_comes_back_whole()
    {
        var reports = RawInputBuffer.Reports(Build(RawInputBuffer.TypeHid, 4, 1, 4, 0, 0, 39));

        Assert.Equal(new byte[] { 4, 0, 0, 39 }, Assert.Single(reports));
    }

    [Fact]
    public void One_nine_byte_report_comes_back_whole()
    {
        var payload = new byte[] { 9, 0, 1, 3, 0, 60, 0, 0, 0 };

        var reports = RawInputBuffer.Reports(Build(RawInputBuffer.TypeHid, 9, 1, payload));

        Assert.Equal(payload, Assert.Single(reports));
    }

    [Fact]
    public void A_batched_buffer_is_split_into_its_reports()
    {
        // dwCount above 1 is legal and does happen; splitting on dwSizeHid is the only way to
        // read the second report at all.
        var reports = RawInputBuffer.Reports(
            Build(RawInputBuffer.TypeHid, 4, 3, 4, 0, 0, 37, 4, 1, 25, 0, 4, 0, 0, 137));

        Assert.Equal(3, reports.Count);
        Assert.Equal(new byte[] { 4, 0, 0, 37 }, reports[0]);
        Assert.Equal(new byte[] { 4, 1, 25, 0 }, reports[1]);
        Assert.Equal(new byte[] { 4, 0, 0, 137 }, reports[2]);
    }

    [Theory]
    [InlineData(0u)] // RIM_TYPEMOUSE
    [InlineData(1u)] // RIM_TYPEKEYBOARD
    [InlineData(7u)]
    public void Anything_that_is_not_a_hid_report_yields_nothing(uint type)
    {
        Assert.Empty(RawInputBuffer.Reports(Build(type, 4, 1, 4, 0, 0, 39)));
    }

    [Fact]
    public void A_buffer_shorter_than_its_own_header_yields_nothing()
    {
        Assert.Empty(RawInputBuffer.Reports(new byte[RawInputBuffer.HeaderSize]));
        Assert.Empty(RawInputBuffer.Reports(new byte[4]));
        Assert.Empty(RawInputBuffer.Reports(Array.Empty<byte>()));
    }

    [Fact]
    public void A_buffer_that_stops_inside_the_hid_prologue_yields_nothing()
    {
        // dwSizeHid and dwCount straddle bytes 24..31. A buffer that ends anywhere in there would
        // have BitConverter reading off the end, so the length check has to cover the prologue and
        // not just the header.
        for (var length = RawInputBuffer.HeaderSize; length <= RawInputBuffer.HeaderSize + 8; length++)
        {
            var stub = new byte[length];
            BitConverter.GetBytes((uint)RawInputBuffer.TypeHid).CopyTo(stub, 0);

            Assert.Empty(RawInputBuffer.Reports(stub));
        }
    }

    [Fact]
    public void A_buffer_that_promises_more_than_it_holds_yields_nothing()
    {
        // Truncation must not be read as a short report, and the arithmetic must not overflow
        // into a negative length: this runs on data the app did not write.
        var truncated = Build(RawInputBuffer.TypeHid, 9, 4, 9, 0, 1, 3);

        Assert.Empty(RawInputBuffer.Reports(truncated));
    }

    [Fact]
    public void A_buffer_holding_only_some_of_the_reports_it_promises_yields_nothing()
    {
        // Two whole reports are present and two are missing. All-or-nothing rather than partial:
        // if dwCount is wrong then dwSizeHid is not evidently right either, and reports split on a
        // size we cannot trust are not reports. Missing a keypress beats acting on a wrong one.
        var half = Build(RawInputBuffer.TypeHid, 4, 4, 4, 0, 0, 37, 4, 0, 0, 38);

        Assert.Empty(RawInputBuffer.Reports(half));
    }

    [Fact]
    public void Bytes_past_the_last_report_are_ignored()
    {
        // The caller may hand over a buffer longer than the packet - see the contract on
        // Reports - and whatever trails the reports is not a report.
        var padded = Build(RawInputBuffer.TypeHid, 4, 1, 4, 0, 0, 39, 0xDE, 0xAD, 0xBE, 0xEF);

        Assert.Equal(new byte[] { 4, 0, 0, 39 }, Assert.Single(RawInputBuffer.Reports(padded)));
    }

    [Fact]
    public void A_header_that_lies_about_its_own_size_changes_nothing()
    {
        // dwSize sits at offset 4 and is never read: every offset is checked against the array
        // that actually exists. A hostile value there must not widen or narrow the walk.
        var lying = Build(RawInputBuffer.TypeHid, 4, 1, 4, 0, 0, 39);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(lying, 4);

        Assert.Equal(new byte[] { 4, 0, 0, 39 }, Assert.Single(RawInputBuffer.Reports(lying)));
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 0u)]
    [InlineData(uint.MaxValue, 1u)]
    [InlineData(4u, uint.MaxValue)]
    public void A_nonsense_size_or_count_yields_nothing(uint sizeHid, uint count)
    {
        Assert.Empty(RawInputBuffer.Reports(Build(RawInputBuffer.TypeHid, sizeHid, count, 4, 0, 0, 39)));
    }

    [Theory]
    [InlineData(0x40000000u, 4u)]      // product is exactly 2^32: wraps to 0 in 32-bit arithmetic
    [InlineData(0x00010000u, 0x00010000u)] // and so does this one
    [InlineData(uint.MaxValue, uint.MaxValue)]
    [InlineData(0x80000000u, 1u)]      // a single "report" whose size alone is negative as an int
    public void A_size_and_count_that_would_wrap_thirty_two_bit_arithmetic_yield_nothing(
        uint sizeHid, uint count)
    {
        // These are the shapes that turn a length check into its own opposite. The caps below
        // reject them long before any multiply, which is the point of having caps at all.
        Assert.Empty(RawInputBuffer.Reports(Build(RawInputBuffer.TypeHid, sizeHid, count, 4, 0, 0, 39)));
    }

    [Fact]
    public void The_caps_are_small_enough_that_no_product_of_them_can_wrap()
    {
        // The guard that survives someone raising the constants: as long as this holds, the
        // per-report and per-buffer caps alone make the offset arithmetic unable to overflow,
        // whatever the buffer claims.
        Assert.InRange(RawInputBuffer.MaxReportBytes, 1, 4096);
        Assert.InRange(RawInputBuffer.MaxReports, 1, 4096);
        Assert.True((long)RawInputBuffer.MaxReportBytes * RawInputBuffer.MaxReports < int.MaxValue);
    }

    [Fact]
    public void A_report_longer_than_anything_this_keyboard_sends_yields_nothing()
    {
        var oversized = Build(RawInputBuffer.TypeHid, RawInputBuffer.MaxReportBytes + 1, 1,
            new byte[RawInputBuffer.MaxReportBytes + 1]);

        Assert.Empty(RawInputBuffer.Reports(oversized));
    }

    [Fact]
    public void More_reports_than_one_message_could_sanely_carry_yields_nothing()
    {
        var flood = Build(RawInputBuffer.TypeHid, 4, RawInputBuffer.MaxReports + 1,
            new byte[4 * (RawInputBuffer.MaxReports + 1)]);

        Assert.Empty(RawInputBuffer.Reports(flood));
    }

    [Fact]
    public void A_buffer_exactly_at_both_caps_is_read_in_full()
    {
        // The caps are limits, not exclusions: the largest well-formed buffer still comes back.
        var payload = new byte[RawInputBuffer.MaxReportBytes * RawInputBuffer.MaxReports];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        var reports = RawInputBuffer.Reports(Build(
            RawInputBuffer.TypeHid, RawInputBuffer.MaxReportBytes, RawInputBuffer.MaxReports, payload));

        Assert.Equal(RawInputBuffer.MaxReports, reports.Count);
        Assert.All(reports, r => Assert.Equal(RawInputBuffer.MaxReportBytes, r.Length));
        Assert.Equal(payload[RawInputBuffer.MaxReportBytes], reports[1][0]);
    }

    [Fact]
    public void A_null_buffer_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => RawInputBuffer.Reports(null!));
    }

    [Fact]
    public void The_reports_it_returns_survive_the_buffer_being_reused()
    {
        // The real caller reuses one stack buffer across messages. A returned report that
        // aliased it would change under the decoder's feet.
        var buffer = Build(RawInputBuffer.TypeHid, 4, 1, 4, 0, 0, 39);

        var report = Assert.Single(RawInputBuffer.Reports(buffer));
        Array.Clear(buffer);

        Assert.Equal(new byte[] { 4, 0, 0, 39 }, report);
    }

    [Fact]
    public void Two_identical_reports_come_back_as_two_arrays()
    {
        // Deduplicating identical reports here would swallow a genuine double tap; that decision
        // belongs to SignalDebouncer, which can see the clock.
        var reports = RawInputBuffer.Reports(
            Build(RawInputBuffer.TypeHid, 4, 2, 4, 0, 0, 37, 4, 0, 0, 37));

        Assert.Equal(2, reports.Count);
        Assert.NotSame(reports[0], reports[1]);
        Assert.Equal(reports[0], reports[1]);
    }

    [Fact]
    public void Everything_it_returns_can_be_handed_straight_to_the_decoder()
    {
        // The seam these two meet at: whatever comes out of the walk is a legal argument to
        // Decode, including the lengths it does not understand.
        var buffer = Build(RawInputBuffer.TypeHid, 4, 2, 4, 0, 0, 39, 4, 1, 25, 0);

        var decoded = RawInputBuffer.Reports(buffer).Select(RawInputDecoder.Decode).ToList();

        Assert.Equal(new HotkeyEvent(HotkeySignal.FanModeAutoHigh), decoded[0]);
        Assert.Equal(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 50), decoded[1]);
    }
}
