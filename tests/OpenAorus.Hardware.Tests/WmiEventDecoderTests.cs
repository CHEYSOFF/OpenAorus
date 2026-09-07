using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The <c>Data</c> values <c>GB_WMIACPI_Event</c> delivers, decoded.
/// </summary>
/// <remarks>
/// Two things here are readings of Gigabyte's decompiled code rather than observations: the
/// property is named <c>Data</c>, and its value is an integer. Nobody has seen one of these events
/// on this chassis. The tests pin both readings and pin the refusal to guess past them; VERIFY 7.1
/// is what settles whether the subscription delivers anything at all.
/// </remarks>
public class WmiEventDecoderTests
{
    [Theory]
    [InlineData(202, HotkeySignal.TouchpadDisabled)]
    [InlineData(458, HotkeySignal.TouchpadEnabled)]
    [InlineData(450, HotkeySignal.WifiEnabled)]
    [InlineData(194, HotkeySignal.WifiDisabled)]
    public void The_four_documented_data_values_decode(int data, HotkeySignal expected)
    {
        Assert.Equal(new HotkeyEvent(expected), WmiEventDecoder.Decode(data));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(203)]
    [InlineData(451)]
    [InlineData(int.MaxValue)]
    public void Anything_else_decodes_to_nothing(int data)
    {
        Assert.Null(WmiEventDecoder.Decode(data));
    }

    [Fact]
    public void The_subscription_names_the_class_the_research_names()
    {
        // The listener builds its watcher from these, so a typo here is a subscription that
        // never fires - and there is no way to see that without the laptop.
        Assert.Equal("GB_WMIACPI_Event", WmiEventDecoder.EventClass);
        Assert.Contains(WmiEventDecoder.EventClass, WmiEventDecoder.Query, StringComparison.Ordinal);
        Assert.StartsWith("SELECT * FROM ", WmiEventDecoder.Query, StringComparison.Ordinal);
        Assert.Equal("Data", WmiEventDecoder.DataProperty);
    }

    [Fact]
    public void Touchpad_and_wifi_are_the_only_things_this_channel_says()
    {
        // Brightness also arrives on this class, carrying a Brightness property rather than a
        // Data value. It is not decoded here: Windows already draws that overlay, and the whole
        // point of v0.3 is not to draw a second one.
        var decoded = Enumerable.Range(0, 1024)
            .Select(WmiEventDecoder.Decode)
            .Where(e => e is not null)
            .Select(e => e!.Signal)
            .ToList();

        Assert.Equal(4, decoded.Count);
        Assert.DoesNotContain(HotkeySignal.DisplayBrightness, decoded);
    }

    [Fact]
    public void The_four_signals_are_four_distinct_signals()
    {
        // Enabled and disabled are kept apart rather than collapsed into "touchpad changed", so
        // an overlay can say which way it went without asking the hardware.
        var decoded = new[] { 202, 458, 450, 194 }
            .Select(d => WmiEventDecoder.Decode(d)!.Signal)
            .ToList();

        Assert.Equal(4, decoded.Distinct().Count());
    }

    [Theory]
    [InlineData(0xCB)]  // 203 - touchpad-off code plus one
    [InlineData(0xC3)]  // 195 - Wi-Fi-off code plus one
    [InlineData(0x1CB)] // 459
    [InlineData(0x1C3)] // 451
    [InlineData(0x2CA)] // touchpad-off with a second high bit set
    [InlineData(0x0D2)] // 210 - a plausible third device under the same reading
    [InlineData(0x1D2)] // 466 - and its "enabled" partner
    public void The_bit_pattern_the_four_values_suggest_is_not_read_as_a_rule(int data)
    {
        // The four look like a device code with 0x100 meaning "on": 0xCA touchpad, 0xC2 Wi-Fi.
        // Nothing documents that structure, so it is not implemented. A chassis that spoke a
        // fifth value has to surface as unknown rather than as a confident wrong answer.
        Assert.Null(WmiEventDecoder.Decode(data));
    }

    [Fact]
    public void An_event_without_the_data_property_is_not_an_event()
    {
        // What a brightness event looks like from here: it carries Brightness instead, so the
        // listener finds no Data property at all and hands on null.
        Assert.Null(WmiEventDecoder.Decode((object?)null));
    }

    [Theory]
    [InlineData((byte)194, HotkeySignal.WifiDisabled)]
    [InlineData((short)458, HotkeySignal.TouchpadEnabled)]
    [InlineData((ushort)458, HotkeySignal.TouchpadEnabled)]
    [InlineData(450, HotkeySignal.WifiEnabled)]
    [InlineData((uint)450, HotkeySignal.WifiEnabled)]
    [InlineData((long)202, HotkeySignal.TouchpadDisabled)]
    [InlineData((ulong)202, HotkeySignal.TouchpadDisabled)]
    public void Any_integer_type_the_provider_might_box_the_value_in_decodes(object value, HotkeySignal expected)
    {
        // Nothing confirms the CIM type of Data - uint32 is the likely one, but the recovered
        // code converts rather than casts. Every integral type is widened and range-checked so a
        // provider that types the property differently is not silently deaf.
        Assert.Equal(new HotkeyEvent(expected), WmiEventDecoder.Decode(value));
    }

    [Fact]
    public void A_signed_byte_is_not_reinterpreted_to_make_a_documented_value_fit()
    {
        // (sbyte)-62 has the same eight bits as (byte)194. It widens to -62, which is not a
        // documented value, and that is the whole answer: the bits are never re-read.
        Assert.Null(WmiEventDecoder.Decode((object)(sbyte)-62));
        Assert.Equal(new HotkeyEvent(HotkeySignal.WifiDisabled), WmiEventDecoder.Decode((object)(byte)194));
    }

    [Fact]
    public void A_number_too_large_for_an_int_decodes_to_nothing_rather_than_overflowing()
    {
        // Convert.ToInt32 would throw here, on a WMI callback thread, where the exception is
        // fatal and nobody ever sees it.
        Assert.Null(WmiEventDecoder.Decode((object)long.MaxValue));
        Assert.Null(WmiEventDecoder.Decode((object)long.MinValue));
        Assert.Null(WmiEventDecoder.Decode((object)ulong.MaxValue));
        Assert.Null(WmiEventDecoder.Decode((object)uint.MaxValue));
        Assert.Null(WmiEventDecoder.Decode((object)((long)int.MaxValue + 1)));
    }

    [Fact]
    public void A_value_shaped_like_nothing_the_research_describes_decodes_to_nothing()
    {
        // "202" as text and 202.0 as a real would both convert to 202, and both would mean the
        // property is not the shape this app assumed. That belongs in VERIFY 7.1 as "the
        // touchpad key does nothing", not papered over by a coercion nobody has justified.
        Assert.Null(WmiEventDecoder.Decode("202"));
        Assert.Null(WmiEventDecoder.Decode((object)202.0));
        Assert.Null(WmiEventDecoder.Decode((object)202.0f));
        Assert.Null(WmiEventDecoder.Decode((object)202m));
        Assert.Null(WmiEventDecoder.Decode((object)true));
        Assert.Null(WmiEventDecoder.Decode((object)(char)202));
        Assert.Null(WmiEventDecoder.Decode(new byte[] { 202 }));
        Assert.Null(WmiEventDecoder.Decode(new[] { 202 }));
        Assert.Null(WmiEventDecoder.Decode(new object()));
    }

    [Fact]
    public void Nothing_a_wmi_event_can_carry_makes_this_throw()
    {
        // This runs on the event watcher's callback thread. An exception there kills nothing
        // visibly and stops the channel, so every unexpected shape has to come back as null.
        var hostile = new object?[]
        {
            null, 0, -1, int.MinValue, int.MaxValue, long.MinValue, long.MaxValue, ulong.MaxValue,
            uint.MaxValue, double.NaN, double.PositiveInfinity, "", "202", "not a number",
            true, 202m, Array.Empty<byte>(), new object(), new object?[] { null }, StringComparison.Ordinal,
        };

        foreach (var value in hostile)
            Assert.Null(Record.Exception(() => WmiEventDecoder.Decode(value)));
    }

    [Fact]
    public void The_boxed_and_unboxed_paths_agree_everywhere()
    {
        for (var data = -8; data < 1024; data++)
            Assert.Equal(WmiEventDecoder.Decode(data), WmiEventDecoder.Decode((object)data));
    }

    [Fact]
    public void The_listener_reads_the_value_through_the_same_pure_conversion()
    {
        // WmiEventListener needs the number itself, not only what it decodes to, and the only
        // other way to get it down there is a Convert.ToInt32 in a catch block that no test can
        // reach. Everything the conversion accepts, it accepts identically for both callers.
        Assert.True(WmiEventDecoder.TryReadData(202, out var boxedInt));
        Assert.Equal(202, boxedInt);
        Assert.True(WmiEventDecoder.TryReadData((ushort)458, out var boxedUshort));
        Assert.Equal(458, boxedUshort);
        Assert.True(WmiEventDecoder.TryReadData(194L, out var boxedLong));
        Assert.Equal(194, boxedLong);
    }

    [Fact]
    public void The_conversion_refuses_what_the_decoder_refuses_rather_than_coercing_it()
    {
        // Convert.ToInt32 would turn every one of these into a keypress or an exception on a
        // callback thread. A value of the wrong shape means the property is not what this app
        // assumed, and that belongs in the trace as an unreadable event, not in a coercion.
        foreach (var value in new object?[] { null, "202", 202.0, 202m, true, ulong.MaxValue, new object() })
        {
            Assert.False(WmiEventDecoder.TryReadData(value, out var data), $"{value ?? "null"}");
            Assert.Equal(0, data);
        }
    }

    [Fact]
    public void The_conversion_and_the_boxed_decode_never_disagree()
    {
        foreach (var value in new object?[] { null, 202, 458, 450, 194, 0, -1, "202", 7.5, uint.MaxValue, (byte)202 })
            Assert.Equal(
                WmiEventDecoder.TryReadData(value, out var data) ? WmiEventDecoder.Decode(data) : null,
                WmiEventDecoder.Decode(value));
    }
}
