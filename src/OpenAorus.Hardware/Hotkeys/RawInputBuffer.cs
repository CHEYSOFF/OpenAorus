namespace OpenAorus.Hardware.Hotkeys;

/// <summary>
/// Reads the HID input reports out of the byte buffer <c>GetRawInputData</c> fills in.
/// </summary>
/// <remarks>
/// <para>
/// Pulled out of the window that receives <c>WM_INPUT</c> so it can be handed a hand-built buffer
/// by a test. The layout is the x64 one, which is the only one this app is built for
/// (<c>RuntimeIdentifier win-x64</c>): a 24-byte <c>RAWINPUTHEADER</c> - <c>dwType</c>,
/// <c>dwSize</c>, <c>hDevice</c>, <c>wParam</c> - then <c>RAWHID</c>'s <c>dwSizeHid</c> and
/// <c>dwCount</c>, then <c>dwCount</c> reports of <c>dwSizeHid</c> bytes each.
/// </para>
/// <para>
/// A READING, NOT AN OBSERVATION. The 24 is <c>hDevice</c> and <c>wParam</c> being pointer-sized
/// and eight-aligned on x64; the header is 16 bytes on x86, and nothing here would work there.
/// Nobody has yet watched a real <c>WM_INPUT</c> buffer come out of this machine. If the offset is
/// wrong, <c>dwSizeHid</c> and <c>dwCount</c> read as garbage, every buffer is rejected by the
/// caps below, and no Fn key ever does anything - a silent, total failure rather than a wrong
/// action. <c>VERIFY.md</c> step 7.2, pressing each Fn combination and watching for exactly one
/// action, is what disproves it.
/// </para>
/// <para>
/// Everything it cannot make sense of yields no reports rather than throwing. This runs on data
/// the app did not write, on every raw-input message the system delivers, so a throw here would
/// be a fault raised from inside a window procedure - where there is no caller to catch it and
/// nothing on screen to say what happened. Rejection is all-or-nothing: a buffer that does not
/// hold what its own fields promise has fields this code has no reason to trust, and reports
/// split on a length that might be wrong are not reports. Missing a keypress beats acting on one
/// that was never pressed, which matters because <c>RIDEV_INPUTSINK</c> delivers these regardless
/// of focus.
/// </para>
/// </remarks>
public static class RawInputBuffer
{
    /// <summary><c>RIM_TYPEHID</c>.</summary>
    public const int TypeHid = 2;

    /// <summary>Size of <c>RAWINPUTHEADER</c> on x64.</summary>
    public const int HeaderSize = 24;

    /// <summary>The longest report this app will read out of a buffer.</summary>
    /// <remarks>The documented collections send 4 and 9 bytes; anything an order of magnitude
    /// past that is not something this app understands, and refusing it bounds the work done per
    /// message.</remarks>
    public const int MaxReportBytes = 64;

    /// <summary>The most reports this app will read out of one buffer.</summary>
    public const int MaxReports = 16;

    /// <summary>The reports inside one <c>RAWINPUT</c> buffer.</summary>
    /// <param name="buffer">The bytes <c>GetRawInputData</c> filled in. Bytes past the last report
    /// are ignored, so a reused receive buffer is safe to pass whole; passing only the length the
    /// call reported is still tighter, because then a <c>dwCount</c> that overstates the packet
    /// cannot be satisfied by stale bytes left over from an earlier message.</param>
    /// <returns>One byte array per report, copied out of the buffer; empty for anything that is
    /// not a well-formed HID message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
    public static IReadOnlyList<byte[]> Reports(byte[] buffer)
    {
        // Null is this app calling itself wrongly, not something the system can deliver - the
        // same line RawInputDecoder draws. Every other malformation below returns empty.
        ArgumentNullException.ThrowIfNull(buffer);

        // Covers the prologue as well as the header: dwSizeHid and dwCount straddle bytes 24..31,
        // and a buffer that ends inside them has no reports to describe anyway.
        if (buffer.Length <= HeaderSize + 8) return Array.Empty<byte[]>();
        if (BitConverter.ToUInt32(buffer, 0) != TypeHid) return Array.Empty<byte[]>();

        // dwSize, at offset 4, is never read. It is the system's own claim about the packet, and
        // believing a claim over the array that actually exists is how this kind of walk goes
        // wrong; every offset below is checked against buffer.Length instead.
        var sizeHid = BitConverter.ToUInt32(buffer, HeaderSize);
        var count = BitConverter.ToUInt32(buffer, HeaderSize + 4);

        // The caps do the real work: past this line sizeHid and count are small, so no product or
        // offset formed from them can leave the range of an int, whatever the buffer claimed.
        if (sizeHid is 0 or > MaxReportBytes) return Array.Empty<byte[]>();
        if (count is 0 or > MaxReports) return Array.Empty<byte[]>();

        var start = HeaderSize + 8;
        // Widened before multiplying anyway. The caps above make this unreachable today, but the
        // failure it guards against - a 32-bit product wrapping into a length that looks like it
        // fits - is silent, and the guard has to outlive whoever next raises a constant.
        var total = (long)sizeHid * count;
        if (start + total > buffer.Length) return Array.Empty<byte[]>();

        var size = (int)sizeHid;
        var reports = new List<byte[]>((int)count);
        for (var i = 0; i < count; i++)
        {
            // Copied, not sliced: the caller reuses one buffer across messages, and a report
            // that aliased it would change under the decoder.
            var report = new byte[size];
            Array.Copy(buffer, start + (i * size), report, 0, size);
            reports.Add(report);
        }
        return reports;
    }
}
