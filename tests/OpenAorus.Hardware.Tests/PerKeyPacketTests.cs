using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class PerKeyPacketTests
{
    // Every plane holds a different function of the slot index, so an interleaved or
    // plane-swapped layout cannot accidentally satisfy any of the offset assertions.
    private static RgbColor[] Ramp()
    {
        var colors = new RgbColor[KeyLayout.SlotCount];
        for (var i = 0; i < colors.Length; i++)
            colors[i] = new RgbColor((byte)i, (byte)(255 - i), (byte)(i * 2 % 256));
        return colors;
    }

    [Fact]
    public void Write_produces_two_264_byte_reports_with_the_documented_headers()
    {
        var (first, second) = PerKeyPacket.BuildWrite(Ramp());
        Assert.Equal(264, first.Length);
        Assert.Equal(264, second.Length);
        Assert.Equal(new byte[] { 0x07, 0x06, 0x00, 0x01, 0, 0, 0, 0 }, first[..8]);
        Assert.Equal(new byte[] { 0x07, 0x06, 0x00, 0x02, 0, 0, 0, 0 }, second[..8]);
    }

    [Fact]
    public void Header_length_is_eight()
        => Assert.Equal(8, PerKeyPacket.HeaderLength);

    [Fact]
    public void Colours_are_laid_out_plane_major_across_the_two_reports()
    {
        var colors = Ramp();
        var (first, second) = PerKeyPacket.BuildWrite(colors);

        for (var i = 0; i < 128; i++)
        {
            Assert.Equal(colors[i].R, first[8 + i]);
            Assert.Equal(colors[i].G, first[8 + 128 + i]);
            Assert.Equal(colors[i].B, second[8 + i]);
        }
    }

    /// <summary>
    /// Pins the plane boundaries against the protocol reference by absolute offset, with
    /// a payload where a single wrong byte is visible: only slot 0 and slot 127 are lit,
    /// so every other payload byte must be zero. Interleaving, a swapped green and blue
    /// plane, or an off-by-one plane start each break at least one of these.
    /// </summary>
    [Fact]
    public void Plane_boundaries_sit_at_the_offsets_the_protocol_reference_gives()
    {
        var colors = new RgbColor[KeyLayout.SlotCount];
        colors[0] = new RgbColor(0x11, 0x22, 0x33);
        colors[127] = new RgbColor(0x44, 0x55, 0x66);

        var (first, second) = PerKeyPacket.BuildWrite(colors);

        // Report one: red plane at 8..135, green plane at 136..263.
        Assert.Equal(0x11, first[8]);
        Assert.Equal(0x44, first[135]);
        Assert.Equal(0x22, first[136]);
        Assert.Equal(0x55, first[263]);

        // Report two: blue plane at 8..135, then padding.
        Assert.Equal(0x33, second[8]);
        Assert.Equal(0x66, second[135]);

        var lit = new HashSet<int> { 8, 135, 136, 263 };
        for (var i = 8; i < 264; i++)
        {
            if (!lit.Contains(i)) Assert.Equal(0, first[i]);
            if (i is not (8 or 135)) Assert.Equal(0, second[i]);
        }
    }

    [Fact]
    public void The_first_report_is_payload_all_the_way_to_its_last_byte()
    {
        var colors = new RgbColor[KeyLayout.SlotCount];
        colors[127] = new RgbColor(0x00, 0x7F, 0x00);
        var (first, _) = PerKeyPacket.BuildWrite(colors);

        // The green plane ends exactly on the report boundary: 8 + 128 + 128 == 264.
        Assert.Equal(0x7F, first[^1]);
    }

    [Fact]
    public void The_tail_of_the_second_report_is_padding()
        => Assert.All(PerKeyPacket.BuildWrite(Ramp()).Second[(8 + 128)..], b => Assert.Equal(0, b));

    [Fact]
    public void Read_requests_use_command_0x86_and_the_two_page_selectors()
    {
        var (first, second) = PerKeyPacket.BuildRead();
        Assert.Equal(new byte[] { 0x07, 0x86, 0x00, 0x01 }, first[..4]);
        Assert.Equal(new byte[] { 0x07, 0x86, 0x00, 0x02 }, second[..4]);
        Assert.All(first[4..], b => Assert.Equal(0, b));
        Assert.All(second[4..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Read_requests_are_264_byte_reports()
    {
        var (first, second) = PerKeyPacket.BuildRead();
        Assert.Equal(264, first.Length);
        Assert.Equal(264, second.Length);
    }

    [Fact]
    public void Parse_is_the_inverse_of_BuildWrite()
    {
        var colors = Ramp();
        var (first, second) = PerKeyPacket.BuildWrite(colors);
        Assert.Equal(colors, PerKeyPacket.Parse(first, second));
    }

    [Fact]
    public void Parse_reads_the_planes_from_their_documented_offsets()
    {
        var first = new byte[KeyboardHid.ReportLength];
        var second = new byte[KeyboardHid.ReportLength];

        first[8] = 0x11;
        first[135] = 0x44;
        first[136] = 0x22;
        first[263] = 0x55;
        second[8] = 0x33;
        second[135] = 0x66;
        // Junk in the second report's padding must be ignored, not read as colour.
        for (var i = 136; i < 264; i++) second[i] = 0xEE;

        var colors = PerKeyPacket.Parse(first, second);

        Assert.Equal(KeyLayout.SlotCount, colors.Length);
        Assert.Equal(new RgbColor(0x11, 0x22, 0x33), colors[0]);
        Assert.Equal(new RgbColor(0x44, 0x55, 0x66), colors[127]);
        Assert.All(colors[1..127], c => Assert.Equal(default, c));
    }

    [Fact]
    public void Parse_ignores_the_response_headers()
    {
        var (first, second) = PerKeyPacket.BuildWrite(Ramp());
        var expected = PerKeyPacket.Parse(first, second);

        // A response's own header is the device's business; only the payload is colour.
        for (var i = 0; i < PerKeyPacket.HeaderLength; i++) { first[i] = 0x5A; second[i] = 0xA5; }

        Assert.Equal(expected, PerKeyPacket.Parse(first, second));
    }

    [Fact]
    public void BuildWrite_rejects_a_wrong_number_of_colours()
    {
        Assert.Throws<ArgumentException>(() => PerKeyPacket.BuildWrite(new RgbColor[127]));
        Assert.Throws<ArgumentException>(() => PerKeyPacket.BuildWrite(new RgbColor[129]));
    }

    [Fact]
    public void Parse_rejects_short_responses()
    {
        Assert.Throws<ArgumentException>(() => PerKeyPacket.Parse(new byte[8], new byte[264]));
        Assert.Throws<ArgumentException>(() => PerKeyPacket.Parse(new byte[264], new byte[8]));
    }

    [Fact]
    public void Parse_rejects_over_long_responses()
    {
        Assert.Throws<ArgumentException>(() => PerKeyPacket.Parse(new byte[265], new byte[264]));
        Assert.Throws<ArgumentException>(() => PerKeyPacket.Parse(new byte[264], new byte[265]));
    }

    [Fact]
    public void Nulls_are_rejected_as_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => PerKeyPacket.BuildWrite(null!));
        Assert.Throws<ArgumentNullException>(() => PerKeyPacket.Parse(null!, new byte[264]));
        Assert.Throws<ArgumentNullException>(() => PerKeyPacket.Parse(new byte[264], null!));
    }
}
