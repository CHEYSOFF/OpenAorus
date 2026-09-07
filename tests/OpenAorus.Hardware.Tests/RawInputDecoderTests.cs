using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The 4-byte and 9-byte HID reports the keyboard's vendor collections deliver, decoded.
/// </summary>
/// <remarks>
/// Everything here is a reading of Gigabyte's decompiled field names, not an observation, and
/// the reading that matters most is that <c>bRawData1</c> is <c>report[0]</c>. The tests pin
/// the reading rather than proving it; hardware settles it.
/// </remarks>
public class RawInputDecoderTests
{
    [Theory]
    [InlineData(137, HotkeySignal.LaunchRecovery)]
    [InlineData(138, HotkeySignal.LaunchUpdateAll)]
    [InlineData(139, HotkeySignal.LaunchUpdateAllDefault)]
    public void The_launch_codes_decode_from_a_four_byte_report(int code, HotkeySignal expected)
    {
        var e = RawInputDecoder.Decode(new byte[] { 4, 0, 0, (byte)code });
        Assert.Equal(new HotkeyEvent(expected), e);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 50)]
    [InlineData(50, 100)]
    public void The_backlight_report_carries_the_level_the_firmware_moved_to(int wire, int percent)
    {
        var e = RawInputDecoder.Decode(new byte[] { 4, 1, (byte)wire, 0 });
        Assert.Equal(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, percent), e);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(75)]
    [InlineData(255)]
    public void A_backlight_level_nobody_documented_is_not_guessed_at(int wire)
    {
        // Doubling the byte would invent a scale that was never measured. A keyboard with four
        // steps has to show up as "not understood", not as a plausible wrong number.
        Assert.Null(RawInputDecoder.Decode(new byte[] { 4, 1, (byte)wire, 0 }));
    }

    [Theory]
    [InlineData(37, HotkeySignal.FanModeStealth)]
    [InlineData(38, HotkeySignal.FanModeAutoLow)]
    [InlineData(39, HotkeySignal.FanModeAutoHigh)]
    public void The_three_fan_codes_stay_distinct_signals(int code, HotkeySignal expected)
    {
        // Kept apart rather than collapsed into one "fan key", so the recovered information
        // survives: honouring the named mode instead of cycling is then a switch change.
        Assert.Equal(new HotkeyEvent(expected), RawInputDecoder.Decode(new byte[] { 4, 0, 0, (byte)code }));
        Assert.Equal(new HotkeyEvent(expected), RawInputDecoder.Decode(new byte[] { 9, 7, 3, (byte)code }));
    }

    [Fact]
    public void An_exact_pattern_wins_over_the_fan_wildcard()
    {
        // The fan rows match on the fourth byte alone. A backlight report whose fourth byte
        // happens to be 37 is still a backlight report.
        var e = RawInputDecoder.Decode(new byte[] { 4, 1, 25, 37 });
        Assert.Equal(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 50), e);
    }

    [Theory]
    [InlineData(0, 0, 38)]
    [InlineData(25, 50, 39)]
    [InlineData(50, 100, 137)]
    public void Every_backlight_level_outranks_whatever_the_fourth_byte_says(int wire, int percent, int trailing)
    {
        // Reordering the cases so the fan wildcard ran first would turn the first two of these
        // into fan-mode changes - a wrong action, not merely a missed one.
        var e = RawInputDecoder.Decode(new byte[] { 4, 1, (byte)wire, (byte)trailing });
        Assert.Equal(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, percent), e);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(38)]
    [InlineData(39)]
    public void A_backlight_report_with_an_unreadable_level_is_still_a_backlight_report(int trailing)
    {
        // `4, 1, _` is matched as backlight before the fan wildcard is ever reached, so an
        // undocumented level stays "not understood" instead of being re-read as a fan code.
        Assert.Null(RawInputDecoder.Decode(new byte[] { 4, 1, 12, (byte)trailing }));
    }

    [Fact]
    public void A_fan_code_and_a_launch_code_a_hundred_apart_are_different_signals()
    {
        // 37 is not 137. This does not pin the case ordering, whatever the arrangement suggests:
        // 37-39 and 137-139 are disjoint, so both orderings agree on all six. The ordering is
        // pinned by Every_backlight_level_outranks_whatever_the_fourth_byte_says.
        Assert.Equal(
            new HotkeyEvent(HotkeySignal.LaunchRecovery),
            RawInputDecoder.Decode(new byte[] { 4, 0, 0, 137 }));
        Assert.Equal(
            new HotkeyEvent(HotkeySignal.FanModeStealth),
            RawInputDecoder.Decode(new byte[] { 4, 0, 0, 37 }));
    }

    [Fact]
    public void The_brightness_report_carries_its_level_in_byte_six()
    {
        var report = new byte[] { 9, 0, 1, 3, 0, 42, 0, 0, 0 };
        Assert.Equal(new HotkeyEvent(HotkeySignal.DisplayBrightness, 42), RawInputDecoder.Decode(report));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(255)]
    public void The_brightness_level_is_passed_through_untouched(int level)
    {
        // Nothing documents a range for this byte, so it is reported as it arrived. The signal
        // is decoded and then deliberately ignored anyway; Windows draws this overlay already.
        var report = new byte[] { 9, 0, 1, 3, 0, (byte)level, 0, 0, 0 };
        Assert.Equal(new HotkeyEvent(HotkeySignal.DisplayBrightness, level), RawInputDecoder.Decode(report));
    }

    [Fact]
    public void The_second_byte_of_a_nine_byte_report_is_a_wildcard()
    {
        // The research writes both 9-byte rows as `9, _, ...`.
        var brightness = new byte[] { 9, 200, 1, 3, 0, 7, 0, 0, 0 };
        Assert.Equal(new HotkeyEvent(HotkeySignal.DisplayBrightness, 7), RawInputDecoder.Decode(brightness));

        var firmware = new byte[] { 9, 200, 200, 23, 0, 0, 0x12, 0x34, 0 };
        Assert.Equal(new HotkeyEvent(HotkeySignal.FirmwareVersionReply), RawInputDecoder.Decode(firmware));
    }

    [Fact]
    public void The_firmware_reply_is_recognised_so_it_is_never_mistaken_for_a_keypress()
    {
        var report = new byte[] { 9, 0, 0, 23, 0, 0, 0x12, 0x34, 0 };
        Assert.Equal(new HotkeyEvent(HotkeySignal.FirmwareVersionReply), RawInputDecoder.Decode(report));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(38)]
    [InlineData(39)]
    public void The_fan_wildcard_does_not_reach_across_into_nine_byte_reports(int code)
    {
        // The research lists the fan rows under the 4-byte table only. Extending them to the
        // 9-byte collection would let an undocumented report from a channel Gigabyte writes to
        // change the fan mode - acting on input we cannot read, which is worse than missing it.
        Assert.Null(RawInputDecoder.Decode(new byte[] { 9, 0, 0, (byte)code, 0, 0, 0, 0, 0 }));
    }

    [Theory]
    [InlineData(new byte[] { 4, 0, 0, 200 })]
    [InlineData(new byte[] { 4, 0, 9, 137 })]
    [InlineData(new byte[] { 4, 2, 0, 0 })]
    [InlineData(new byte[] { 9, 0, 2, 3, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 9, 0, 1, 4, 0, 0, 0, 0, 0 })]
    public void Anything_it_does_not_recognise_exactly_decodes_to_nothing(byte[] report)
    {
        // `4, 0, 9, 137` is the launch row with its third byte changed. The research writes that
        // byte as an exact 0, so launching a recovery tool must not survive losing it: it is the
        // one byte in either table that no other test here holds in place.
        Assert.Null(RawInputDecoder.Decode(report));
    }

    [Theory]
    [InlineData(new byte[] { 7, 0, 1, 3, 0, 42, 0, 0, 0 })]
    [InlineData(new byte[] { 7, 0, 0, 23, 0, 0, 0x12, 0x34, 0 })]
    [InlineData(new byte[] { 0, 0, 1, 3, 0, 42, 0, 0, 0 })]
    public void A_nine_byte_report_whose_first_byte_is_not_nine_decodes_to_nothing(byte[] report)
    {
        // Both 9-byte rows begin `9`. Dropping that check would widen the pattern past what the
        // research documents, on a collection that carries traffic this app has never decoded.
        Assert.Null(RawInputDecoder.Decode(report));
    }

    [Theory]
    [InlineData(new byte[] { 5, 0, 0, 137 })]
    [InlineData(new byte[] { 0, 1, 25, 0 })]
    public void A_four_byte_report_whose_first_byte_is_not_four_matches_no_exact_pattern(byte[] report)
    {
        Assert.Null(RawInputDecoder.Decode(report));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(264)]
    public void A_report_of_any_other_length_decodes_to_nothing(int length)
    {
        Assert.Null(RawInputDecoder.Decode(new byte[length]));
    }

    [Fact]
    public void A_report_shorter_or_longer_than_the_two_known_shapes_never_indexes_out_of_range()
    {
        // This runs inside a window procedure, where an exception is fatal, and the bytes come
        // off a device rather than out of this app. Length is checked before any byte is read.
        for (var length = 0; length <= 64; length++)
        {
            var report = new byte[length];
            for (var i = 0; i < length; i++) report[i] = 37;
            var e = Record.Exception(() => RawInputDecoder.Decode(report));
            Assert.Null(e);
        }
    }

    [Fact]
    public void The_documented_backlight_wire_levels_are_published_as_the_lookup_they_are()
    {
        Assert.Equal(new[] { 0, 25, 50 }, RawInputDecoder.BacklightWireLevels);
        Assert.Equal(4, RawInputDecoder.ShortReportLength);
        Assert.Equal(9, RawInputDecoder.LongReportLength);
    }

    [Fact]
    public void A_null_report_is_a_programming_error_not_an_unknown_key()
    {
        Assert.Throws<ArgumentNullException>(() => RawInputDecoder.Decode(null!));
    }
}
