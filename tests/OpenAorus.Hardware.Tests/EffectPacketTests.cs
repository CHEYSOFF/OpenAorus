using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class EffectPacketTests
{
    private static byte[] Build(LightEffect effect) => EffectPacket.Build(EffectParameters.Default(effect));

    /// <summary>
    /// Slice (offset, length) per effect, hardcoded from docs/research/ione-keyboard-protocol.md.
    /// Deliberately not read from <see cref="EffectPacket.Offsets"/>: a wrong offset there must
    /// not be able to move the assertion window along with the bug.
    /// </summary>
    private static readonly Dictionary<LightEffect, (int Offset, int Length)> DocumentedSlices = new()
    {
        [LightEffect.Static] = (0, 4),
        [LightEffect.Breathing] = (4, 5),
        [LightEffect.Flow] = (9, 2),
        [LightEffect.Firework] = (11, 5),
        [LightEffect.Ripple] = (16, 5),
        [LightEffect.Rain] = (21, 5),
        [LightEffect.Cycling] = (26, 1),
        [LightEffect.Trigger] = (27, 5),
        [LightEffect.Pulse] = (32, 5),
        [LightEffect.Radar] = (37, 6),
        [LightEffect.StarShining] = (43, 5),
        [LightEffect.Wave] = (48, 6),
        [LightEffect.Cross] = (54, 5),
        [LightEffect.Dragonstrike] = (59, 9),
        [LightEffect.Bloom] = (68, 8),
        [LightEffect.Spiral] = (76, 2),
        [LightEffect.Merge] = (78, 8),
        [LightEffect.Crash] = (86, 9),
        [LightEffect.Custom] = (0, 0),
    };

    public static TheoryData<LightEffect> EveryEffect
    {
        get
        {
            var data = new TheoryData<LightEffect>();
            foreach (var effect in Enum.GetValues<LightEffect>()) data.Add(effect);
            return data;
        }
    }

    /// <summary>
    /// Every parameter encodes to a distinct non-zero byte, so that an overflow of any one
    /// field is visible. Under <see cref="EffectParameters.Default"/> several fields encode
    /// to zero and a spill would be indistinguishable from untouched padding.
    /// </summary>
    private static EffectParameters AllFieldsNonZero(LightEffect effect) => new(
        Effect: effect,
        Color: new RgbColor(0x11, 0x22, 0x33),
        SecondColor: new RgbColor(0x44, 0x55, 0x66),
        SpeedPercent: 30,        // wire 7
        BrightnessPercent: 70,
        Direction: LightDirection.Down,  // wire 3 (2 for Flow)
        Random: true);           // wire 1

    [Theory]
    [MemberData(nameof(EveryEffect))]
    public void An_effect_writes_only_inside_its_own_slice(LightEffect effect)
    {
        var (offset, length) = DocumentedSlices[effect];
        var packet = EffectPacket.Build(AllFieldsNonZero(effect));

        var from = 13 + offset;
        var to = from + length;
        for (var i = 2; i < packet.Length; i++)
        {
            // 10 effect id, 11 marker, 12 brightness; the rest of the header (2..9) is
            // required to stay zero and is covered by starting the scan at 2.
            if (i is 10 or 11 or 12) continue;
            if (i >= from && i < to) continue;
            Assert.True(packet[i] == 0,
                $"{effect} wrote 0x{packet[i]:X2} at byte {i}, outside its slice [{from}, {to}).");
        }
    }

    [Theory]
    [MemberData(nameof(EveryEffect))]
    public void Header_bytes_two_to_nine_are_always_zero(LightEffect effect)
    {
        var packet = EffectPacket.Build(AllFieldsNonZero(effect));
        Assert.All(packet[2..10], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Every_packet_is_264_bytes_with_the_report_id_and_set_mode_command()
    {
        foreach (var effect in Enum.GetValues<LightEffect>())
        {
            var packet = Build(effect);
            Assert.Equal(264, packet.Length);
            Assert.Equal(0x07, packet[0]);
            Assert.Equal(0x02, packet[1]);
            Assert.Equal((byte)effect, packet[10]);
        }
    }

    [Fact]
    public void Offset_table_matches_the_documented_protocol()
    {
        var expected = new Dictionary<LightEffect, int>
        {
            [LightEffect.Static] = 0, [LightEffect.Breathing] = 4, [LightEffect.Flow] = 9,
            [LightEffect.Firework] = 11, [LightEffect.Ripple] = 16, [LightEffect.Rain] = 21,
            [LightEffect.Cycling] = 26, [LightEffect.Trigger] = 27, [LightEffect.Pulse] = 32,
            [LightEffect.Radar] = 37, [LightEffect.StarShining] = 43, [LightEffect.Wave] = 48,
            [LightEffect.Cross] = 54, [LightEffect.Dragonstrike] = 59, [LightEffect.Bloom] = 68,
            [LightEffect.Spiral] = 76, [LightEffect.Merge] = 78, [LightEffect.Crash] = 86,
            [LightEffect.Custom] = 0,
        };
        Assert.Equal(expected.Count, EffectPacket.Offsets.Count);
        foreach (var (effect, offset) in expected)
            Assert.Equal(offset, EffectPacket.Offsets[effect]);
    }

    [Fact]
    public void Static_writes_marker_brightness_and_colour_at_the_documented_offsets()
    {
        var p = EffectParameters.Default(LightEffect.Static) with
        {
            Color = new RgbColor(0x11, 0x22, 0x33),
            BrightnessPercent = 60,
        };
        var packet = EffectPacket.Build(p);
        Assert.Equal(0xFF, packet[11]);
        Assert.Equal(60, packet[12]);
        Assert.Equal(0x00, packet[13]);
        Assert.Equal(0x11, packet[14]);
        Assert.Equal(0x22, packet[15]);
        Assert.Equal(0x33, packet[16]);
    }

    [Fact]
    public void Wave_writes_speed_random_direction_and_colour_at_its_own_offset()
    {
        var p = EffectParameters.Default(LightEffect.Wave) with
        {
            Color = new RgbColor(1, 2, 3),
            SpeedPercent = 100,
            Random = true,
            Direction = LightDirection.Left,
        };
        var packet = EffectPacket.Build(p);
        var at = 13 + EffectPacket.Offsets[LightEffect.Wave];
        Assert.Equal(0, packet[at]);            // speed 100 % -> wire 0 (fastest)
        Assert.Equal(1, packet[at + 1]);        // random
        Assert.Equal(1, packet[at + 2]);        // direction Left
        Assert.Equal(1, packet[at + 3]);
        Assert.Equal(2, packet[at + 4]);
        Assert.Equal(3, packet[at + 5]);
    }

    [Fact]
    public void Cycling_writes_only_a_speed_byte()
    {
        var p = EffectParameters.Default(LightEffect.Cycling) with { SpeedPercent = 50 };
        var packet = EffectPacket.Build(p);
        var at = 13 + EffectPacket.Offsets[LightEffect.Cycling];
        Assert.Equal(5, packet[at]);
        Assert.Equal(0, packet[at + 1]);
    }

    [Fact]
    public void Two_colour_effects_write_both_colours()
    {
        var p = EffectParameters.Default(LightEffect.Dragonstrike) with
        {
            Color = new RgbColor(0xAA, 0xBB, 0xCC),
            SecondColor = new RgbColor(0xDD, 0xEE, 0xFF),
        };
        var packet = EffectPacket.Build(p);
        var at = 13 + EffectPacket.Offsets[LightEffect.Dragonstrike];
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, packet[(at + 3)..(at + 6)]);
        Assert.Equal(new byte[] { 0xDD, 0xEE, 0xFF }, packet[(at + 6)..(at + 9)]);
    }

    [Fact]
    public void Two_colour_effects_without_direction_pack_colours_at_offset_two_and_five()
    {
        var p = EffectParameters.Default(LightEffect.Merge) with
        {
            Color = new RgbColor(0x01, 0x02, 0x03),
            SecondColor = new RgbColor(0x04, 0x05, 0x06),
        };
        var packet = EffectPacket.Build(p);
        var at = 13 + EffectPacket.Offsets[LightEffect.Merge];
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, packet[(at + 2)..(at + 5)]);
        Assert.Equal(new byte[] { 0x04, 0x05, 0x06 }, packet[(at + 5)..(at + 8)]);
        // Merge's slice is exactly 8 bytes (78..86): must not spill into Crash's slice.
        Assert.Equal(0, packet[at + 8]);
    }

    [Fact]
    public void Radar_writes_speed_random_direction_and_colour_like_wave()
    {
        var p = EffectParameters.Default(LightEffect.Radar) with
        {
            Color = new RgbColor(9, 8, 7),
            SpeedPercent = 100,
            Random = true,
            Direction = LightDirection.Left,
        };
        var packet = EffectPacket.Build(p);
        var at = 13 + EffectPacket.Offsets[LightEffect.Radar];
        Assert.Equal(0, packet[at]);
        Assert.Equal(1, packet[at + 1]);
        Assert.Equal(1, packet[at + 2]);
        Assert.Equal(new byte[] { 9, 8, 7 }, packet[(at + 3)..(at + 6)]);
    }

    [Fact]
    public void Custom_writes_no_configuration_bytes_beyond_brightness()
    {
        var packet = EffectPacket.Build(EffectParameters.Default(LightEffect.Custom) with { BrightnessPercent = 40 });
        Assert.Equal(0x12, packet[10]);
        Assert.Equal(40, packet[12]);
        Assert.All(packet[13..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(50, 5)]
    [InlineData(100, 0)]
    [InlineData(150, 0)]
    [InlineData(-10, 10)]
    public void Speed_is_inverted_and_clamped_on_the_wire(int ui, int wire)
        => Assert.Equal(wire, EffectPacket.EncodeSpeed(ui));

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(140, 100)]
    public void Brightness_is_clamped_to_0_100(int ui, int expected)
    {
        var packet = EffectPacket.Build(EffectParameters.Default(LightEffect.Static) with { BrightnessPercent = ui });
        Assert.Equal(expected, packet[12]);
    }

    [Theory]
    [InlineData(LightEffect.Breathing)]
    [InlineData(LightEffect.Ripple)]
    public void Effects_without_a_random_flag_never_ship_one(LightEffect effect)
    {
        // The research document gives these a [speed, mode?, R, G, B] shape: the second
        // byte is an unidentified firmware mode selector, not the random flag. Random is
        // carried across effect switches in one EffectParameters record, so enabling it on
        // Firework must not leak a 1 into that selector here.
        Assert.False(EffectPacket.SupportsRandom(effect));
        var packet = EffectPacket.Build(AllFieldsNonZero(effect));
        var at = 13 + DocumentedSlices[effect].Offset;
        Assert.Equal(0, packet[at + 1]);
    }

    [Fact]
    public void Flow_swaps_the_up_and_down_direction_codes()
    {
        // The one direction quirk Gigabyte's code documents: 2 and 3 are swapped for Flow.
        var at = 13 + DocumentedSlices[LightEffect.Flow].Offset;
        var up = EffectPacket.Build(AllFieldsNonZero(LightEffect.Flow) with { Direction = LightDirection.Up });
        var down = EffectPacket.Build(AllFieldsNonZero(LightEffect.Flow) with { Direction = LightDirection.Down });
        Assert.Equal(3, up[at + 1]);
        Assert.Equal(2, down[at + 1]);
    }

    [Fact]
    public void Wave_keeps_the_unswapped_up_and_down_direction_codes()
    {
        var at = 13 + DocumentedSlices[LightEffect.Wave].Offset;
        var up = EffectPacket.Build(AllFieldsNonZero(LightEffect.Wave) with { Direction = LightDirection.Up });
        var down = EffectPacket.Build(AllFieldsNonZero(LightEffect.Wave) with { Direction = LightDirection.Down });
        Assert.Equal(2, up[at + 2]);
        Assert.Equal(3, down[at + 2]);
    }

    [Theory]
    [InlineData(LightEffect.Static, 0xFF)]
    [InlineData(LightEffect.StarShining, 0xFF)]
    [InlineData(LightEffect.Breathing, 0x00)]
    public void Byte_eleven_carries_the_marker_only_for_static_and_star_shining(LightEffect effect, int marker)
        => Assert.Equal(marker, Build(effect)[11]);

    [Fact]
    public void Bloom_packs_both_colours_at_offset_two_and_five_like_merge()
    {
        var p = EffectParameters.Default(LightEffect.Bloom) with
        {
            Color = new RgbColor(0x21, 0x22, 0x23),
            SecondColor = new RgbColor(0x24, 0x25, 0x26),
        };
        var packet = EffectPacket.Build(p);
        var at = 13 + DocumentedSlices[LightEffect.Bloom].Offset;
        Assert.Equal(new byte[] { 0x21, 0x22, 0x23 }, packet[(at + 2)..(at + 5)]);
        Assert.Equal(new byte[] { 0x24, 0x25, 0x26 }, packet[(at + 5)..(at + 8)]);
        // Bloom's slice is exactly 8 bytes (68..76): must not spill into Spiral's slice.
        Assert.Equal(0, packet[at + 8]);
    }

    [Fact]
    public void Crash_writes_speed_random_direction_then_both_colours()
    {
        var p = EffectParameters.Default(LightEffect.Crash) with
        {
            Color = new RgbColor(0x31, 0x32, 0x33),
            SecondColor = new RgbColor(0x34, 0x35, 0x36),
            SpeedPercent = 100,
            Random = true,
            Direction = LightDirection.Left,
        };
        var packet = EffectPacket.Build(p);
        var at = 13 + DocumentedSlices[LightEffect.Crash].Offset;
        Assert.Equal(0, packet[at]);
        Assert.Equal(1, packet[at + 1]);
        Assert.Equal(1, packet[at + 2]);
        Assert.Equal(new byte[] { 0x31, 0x32, 0x33 }, packet[(at + 3)..(at + 6)]);
        Assert.Equal(new byte[] { 0x34, 0x35, 0x36 }, packet[(at + 6)..(at + 9)]);
    }

    [Fact]
    public void An_unknown_effect_id_is_rejected_by_name_and_value()
    {
        // LightEffect is a byte enum and these parameters get deserialised from settings,
        // so a stale or hand-edited id is a realistic input. Throwing rather than clamping
        // is deliberate: an unknown id is a persistence or programming error.
        var p = EffectParameters.Default((LightEffect)0x7F);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => EffectPacket.Build(p));
        Assert.Equal("p", ex.ParamName);
        Assert.Equal((LightEffect)0x7F, ex.ActualValue);
    }

    [Fact]
    public void Build_rejects_null_parameters()
        => Assert.Throws<ArgumentNullException>(() => EffectPacket.Build(null!));

    [Fact]
    public void The_offset_table_cannot_be_cast_back_to_a_mutable_dictionary()
        => Assert.IsNotType<Dictionary<LightEffect, int>>(EffectPacket.Offsets);

    [Fact]
    public void Capability_helpers_describe_each_effect_family()
    {
        Assert.True(EffectPacket.SupportsColor(LightEffect.Static));
        Assert.False(EffectPacket.SupportsSpeed(LightEffect.Static));
        Assert.False(EffectPacket.SupportsColor(LightEffect.Cycling));
        Assert.True(EffectPacket.SupportsSpeed(LightEffect.Cycling));
        Assert.True(EffectPacket.SupportsDirection(LightEffect.Flow));
        Assert.True(EffectPacket.SupportsSecondColor(LightEffect.Merge));
        Assert.False(EffectPacket.SupportsSecondColor(LightEffect.Wave));
        Assert.True(EffectPacket.SupportsRandom(LightEffect.Firework));
        Assert.False(EffectPacket.SupportsColor(LightEffect.Custom));
    }
}
