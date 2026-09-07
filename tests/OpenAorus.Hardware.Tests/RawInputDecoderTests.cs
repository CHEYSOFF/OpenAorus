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
        // Nothing documents a range for this byte, so it is reported as it arrived. The signal is
        // decoded and then deliberately ignored anyway: this report says the brightness has
        // ALREADY changed, so whatever changed it drew its own card. Not to be confused with the
        // two brightness keys below, which say only that a key was pressed.
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

    // ---- Observed on hardware, not decompiled ---------------------------------------------
    //
    // Everything above this line is a reading of Gigabyte's binaries. Everything below was
    // watched arrive on an AORUS 17G KD through the diagnostics trace, and where the two
    // disagree the measurement wins for this chassis. See the "Observed on hardware" section
    // of docs/research/fn-hotkey-signals.md.

    [Theory]
    [InlineData(125, HotkeySignal.PanelBrightnessDown)]
    [InlineData(126, HotkeySignal.PanelBrightnessUp)]
    public void The_two_brightness_keys_this_chassis_really_sends_are_decoded(int code, HotkeySignal expected)
    {
        // 125 is 7D and 126 is 7E. Neither appears anywhere in the recovered tables, which give
        // 137-139 for the launcher keys and 37-39 for fan modes; this chassis sends none of those.
        Assert.Equal(new HotkeyEvent(expected), RawInputDecoder.Decode(new byte[] { 4, 0, 0, (byte)code }));
    }

    [Fact]
    public void The_release_that_follows_a_brightness_press_decodes_to_nothing()
    {
        // Every tap is two reports: `04 00 00 7E` and then `04 00 00 00`. Decoding the release as
        // anything would move the panel two steps for one press, which is the single most likely
        // way for this feature to be wrong in a way the owner notices immediately.
        Assert.Null(RawInputDecoder.Decode(new byte[] { 4, 0, 0, 0 }));
    }

    [Fact]
    public void A_tap_of_a_brightness_key_is_one_signal_and_one_nothing()
    {
        // The pair as it actually arrives, in order, rather than each half in isolation.
        Assert.Equal(
            new HotkeyEvent(HotkeySignal.PanelBrightnessUp),
            RawInputDecoder.Decode(new byte[] { 4, 0, 0, 0x7E }));
        Assert.Null(RawInputDecoder.Decode(new byte[] { 4, 0, 0, 0x00 }));
    }

    [Theory]
    [InlineData(134)]
    [InlineData(135)]
    public void The_two_codes_nobody_has_identified_yet_are_not_acted_on(int code)
    {
        // 86 and 87 on the wire. Recorded by HotkeyTrace, decoded by nothing: a code whose key
        // nobody has named must not be given an action on the strength of being nearby in the
        // number space. The trace is where the next person picks them up.
        Assert.Null(RawInputDecoder.Decode(new byte[] { 4, 0, 1, (byte)code }));
        Assert.Null(RawInputDecoder.Decode(new byte[] { 4, 0, 0, (byte)code }));
    }

    [Fact]
    public void A_non_zero_third_byte_is_a_press_flag_and_not_a_malformed_report()
    {
        // Two release conventions exist on this chassis. The brightness keys clear the code
        // (`04 00 00 7D` then `04 00 00 00`); the 86/87 keys keep it and toggle byte 2 from 1 to
        // 0. So a report with byte 2 set is ordinary traffic, and the decoder has to answer "not
        // a key I know" for it rather than treating it as a shape that cannot happen.
        for (var code = 0; code <= 255; code++)
        {
            var e = Record.Exception(() => RawInputDecoder.Decode(new byte[] { 4, 0, 1, (byte)code }));
            Assert.Null(e);
        }
    }

    [Fact]
    public void The_brightness_keys_are_the_press_only_and_never_the_release_pattern()
    {
        // Guards the one edit that would break the pair above: widening the match to ignore the
        // fourth byte, or matching on byte 2 alone, would turn the release into a second step.
        Assert.Null(RawInputDecoder.Decode(new byte[] { 4, 0, 1, 0x7D }));
        Assert.Null(RawInputDecoder.Decode(new byte[] { 4, 0, 1, 0x7E }));
    }
}
