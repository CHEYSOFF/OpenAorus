namespace OpenAorus.Hardware.Lighting;

/// <summary>
/// Builds and parses the two feature reports that carry the 128 per-key colours
/// (commands 0x06 to write, 0x86 to read).
///
/// Colours are plane-major, not interleaved: the wire carries all 128 reds, then all
/// 128 greens, then all 128 blues — 384 bytes, sliced across two 264-byte reports
/// behind an 8-byte header each. Report one (page 0x01) holds the red and green planes
/// and is full to its last byte (8 + 128 + 128 == 264); report two (page 0x02) holds
/// the blue plane followed by 128 padding bytes. Getting this wrong lights the keyboard
/// in plausible but entirely wrong colours, so the offsets are pinned by test against
/// the protocol reference rather than against this code.
///
/// Reads use the same two page selectors and answer with the same plane order behind an
/// 8-byte header; the header of a response belongs to the device and is not inspected.
///
/// Protocol reconstructed from Gigabyte's own binaries and cross-checked against
/// rcassani/keyboard-fusion-rgb (GPL-3.0). See docs/research/ione-keyboard-protocol.md.
/// </summary>
public static class PerKeyPacket
{
    /// <summary>Bytes before the colour payload in every per-key report and response.</summary>
    public const int HeaderLength = 8;

    private const byte WriteCommand = 0x06;
    private const byte ReadCommand = 0x86;
    private const byte FirstPage = 0x01;
    private const byte SecondPage = 0x02;

    /// <summary>
    /// Packs 128 colours into the two write reports, in slot order.
    /// </summary>
    /// <param name="colors">Exactly <see cref="KeyLayout.SlotCount"/> colours.</param>
    public static (byte[] First, byte[] Second) BuildWrite(IReadOnlyList<RgbColor> colors)
    {
        if (colors is null) throw new ArgumentNullException(nameof(colors));
        if (colors.Count != KeyLayout.SlotCount)
            throw new ArgumentException($"Expected exactly {KeyLayout.SlotCount} colours, got {colors.Count}.", nameof(colors));

        var first = NewReport(WriteCommand, FirstPage);
        var second = NewReport(WriteCommand, SecondPage);

        for (var i = 0; i < KeyLayout.SlotCount; i++)
        {
            first[HeaderLength + i] = colors[i].R;
            first[HeaderLength + KeyLayout.SlotCount + i] = colors[i].G;
            second[HeaderLength + i] = colors[i].B;
        }

        return (first, second);
    }

    /// <summary>
    /// Builds the two read requests. Each is written to the device, then answered by a
    /// <c>GetFeature</c> whose payload feeds <see cref="Parse"/>.
    /// </summary>
    public static (byte[] First, byte[] Second) BuildRead() =>
        (NewReport(ReadCommand, FirstPage), NewReport(ReadCommand, SecondPage));

    /// <summary>
    /// Unpacks the two read responses back into 128 colours in slot order. Only the
    /// payload is read: response headers and the second report's padding are ignored.
    /// </summary>
    public static RgbColor[] Parse(byte[] firstResponse, byte[] secondResponse)
    {
        if (firstResponse is null) throw new ArgumentNullException(nameof(firstResponse));
        if (secondResponse is null) throw new ArgumentNullException(nameof(secondResponse));
        if (firstResponse.Length != KeyboardHid.ReportLength)
            throw new ArgumentException($"First response must be {KeyboardHid.ReportLength} bytes.", nameof(firstResponse));
        if (secondResponse.Length != KeyboardHid.ReportLength)
            throw new ArgumentException($"Second response must be {KeyboardHid.ReportLength} bytes.", nameof(secondResponse));

        var colors = new RgbColor[KeyLayout.SlotCount];
        for (var i = 0; i < KeyLayout.SlotCount; i++)
            colors[i] = new RgbColor(
                firstResponse[HeaderLength + i],
                firstResponse[HeaderLength + KeyLayout.SlotCount + i],
                secondResponse[HeaderLength + i]);
        return colors;
    }

    private static byte[] NewReport(byte command, byte page)
    {
        var report = new byte[KeyboardHid.ReportLength];
        report[0] = KeyboardHid.ReportId;
        report[1] = command;
        report[2] = 0x00;
        report[3] = page;
        return report;
    }
}
