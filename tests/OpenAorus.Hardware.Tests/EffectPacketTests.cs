using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class EffectPacketTests
{
    private static byte[] Build(LightEffect effect) => EffectPacket.Build(EffectParameters.Default(effect));

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
