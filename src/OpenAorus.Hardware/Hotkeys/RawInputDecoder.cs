namespace OpenAorus.Hardware.Hotkeys;

/// <summary>
/// Turns one HID input report from the keyboard's vendor collections into a
/// <see cref="HotkeyEvent"/>, or into nothing.
/// </summary>
/// <remarks>
/// <para>
/// Pure and total: no IO, no state, and every input that is not an exact documented match returns
/// null rather than throwing or guessing. That matters more here than anywhere else in the app,
/// because <c>RIDEV_INPUTSINK</c> delivers these reports regardless of focus - a pattern that
/// matched loosely would act on input meant for another program.
/// </para>
/// <para>
/// THE ASSUMPTION THIS WHOLE RELEASE RESTS ON. The research names the report bytes
/// <c>bRawData1..4</c>, and this reads those 1-based field names as 0-based indices, so
/// <c>bRawData1</c> is <c>report[0]</c>. It is the only reading under which the 4-byte and 9-byte
/// tables in <c>docs/research/fn-hotkey-signals.md</c> agree with each other - the leading byte of
/// every documented pattern is then the report's own length - but it is a reading of decompiled
/// field names, not something anyone has seen on hardware. If it is wrong, every pattern below is
/// off by one byte and every Fn key silently does nothing. <c>VERIFY.md</c> step 8.2, the step
/// that presses each Fn combination and watches for exactly one action, is what disproves it.
/// </para>
/// <para>
/// Under the same reading, "byte 6" of the brightness report is <c>report[5]</c> and the firmware
/// reply's BCD version nibbles in "bytes 7 and 8" are <c>report[6]</c> and <c>report[7]</c>. The
/// version is not carried in the signal, so those two bytes are only ever skipped over.
/// </para>
/// </remarks>
public static class RawInputDecoder
{
    /// <summary>The vendor collection on <c>mi_02&amp;col03</c> delivers reports this long.</summary>
    public const int ShortReportLength = 4;

    /// <summary>The vendor collection on <c>mi_02&amp;col07</c> delivers reports this long.</summary>
    public const int LongReportLength = 9;

    /// <summary>The three backlight levels the firmware is documented to report, on the wire.</summary>
    /// <remarks>0, 25 and 50 mean 0 %, 50 % and 100 %. Nothing else is a level this app claims to
    /// understand: the mapping is a lookup and not arithmetic precisely so a fourth step on some
    /// other chassis surfaces as unknown instead of as a plausible wrong number.</remarks>
    public static IReadOnlyList<int> BacklightWireLevels { get; } = new[] { 0, 25, 50 };

    /// <summary>Decodes one report.</summary>
    /// <param name="report">The report bytes, exactly as they came off the wire.</param>
    /// <returns>The signal, or null if this is not a report this app understands.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    public static HotkeyEvent? Decode(byte[] report)
    {
        // Null is this app calling itself wrongly, not something a device can send; the device's
        // own malformed input is a length or a byte value, and both of those return null below.
        ArgumentNullException.ThrowIfNull(report);

        // Length first, so no byte is ever read from a buffer that might not hold it.
        return report.Length switch
        {
            ShortReportLength => DecodeShort(report),
            LongReportLength => DecodeLong(report),
            _ => null,
        };
    }

    private static HotkeyEvent? DecodeShort(byte[] r)
    {
        // Exact patterns first. The fan wildcard at the bottom matches on r[3] alone, so moving
        // it up would swallow a launch code or a backlight report whose fourth byte landed on
        // 37-39 - a wrong action rather than a missed one.
        if (r[0] == 4 && r[1] == 0 && r[2] == 0)
        {
            switch (r[3])
            {
                case 137: return new HotkeyEvent(HotkeySignal.LaunchRecovery);
                case 138: return new HotkeyEvent(HotkeySignal.LaunchUpdateAll);
                case 139: return new HotkeyEvent(HotkeySignal.LaunchUpdateAllDefault);
            }
        }

        // `4, 1, <level>`, fourth byte unconstrained. Returning here rather than falling through
        // is deliberate: a backlight report whose level is not one of the three documented ones is
        // a backlight report this app cannot read, not an invitation to re-read it as a fan code.
        if (r[0] == 4 && r[1] == 1) return Backlight(r[2]);

        return FanMode(r[3]);
    }

    private static HotkeyEvent? DecodeLong(byte[] r)
    {
        // Both documented 9-byte rows begin with 9, and neither of them is a fan row: the research
        // lists the fan wildcard under the 4-byte table only. This collection carries traffic
        // Gigabyte writes to and nobody has decoded, so nothing here matches loosely.
        if (r[0] != 9) return null;
        if (r[3] == 23) return new HotkeyEvent(HotkeySignal.FirmwareVersionReply);
        if (r[2] == 1 && r[3] == 3) return new HotkeyEvent(HotkeySignal.DisplayBrightness, r[5]);
        return null;
    }

    private static HotkeyEvent? Backlight(byte wire)
    {
        for (var i = 0; i < BacklightWireLevels.Count; i++)
        {
            // 0 -> 0 %, 25 -> 50 %, 50 -> 100 %: the position in the documented table, not a
            // scale. Doubling the byte would fit all three and invent the rest.
            if (BacklightWireLevels[i] == wire)
                return new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, i * 50);
        }
        return null;
    }

    private static HotkeyEvent? FanMode(byte code) => code switch
    {
        37 => new HotkeyEvent(HotkeySignal.FanModeStealth),
        38 => new HotkeyEvent(HotkeySignal.FanModeAutoLow),
        39 => new HotkeyEvent(HotkeySignal.FanModeAutoHigh),
        _ => null,
    };
}
