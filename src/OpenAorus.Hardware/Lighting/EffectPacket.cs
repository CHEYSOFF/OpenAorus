using System.Collections.Frozen;

namespace OpenAorus.Hardware.Lighting;

/// <summary>
/// Builds the 264-byte "set effect" feature report (command 0x02).
///
/// Layout: [0]=0x07 report id, [1]=0x02 command, [2..9]=0, [10]=effect id,
/// [11]=0xFF for Static and StarShining else 0, [12]=brightness,
/// [13 + Offsets[effect] ...]=the effect's own configuration bytes.
/// Each effect owns a private slice, so changing one leaves the others intact — but only
/// if the write carries the rest of the block through. Command 0x82 reads the whole block
/// back, so callers read first and pass it to the two-argument <see cref="Build(EffectParameters, byte[])"/>.
///
/// The configuration shape per effect is not independently documented; it is derived
/// from the gap between an effect's offset and the next one in the table, which must
/// exactly bound that effect's writes (write past it and you corrupt the next effect's
/// stored settings). Two families need special care because of this:
///   - Radar's gap is 6 bytes, matching Wave's [speed, random, direction, R, G, B]
///     shape rather than the 5-byte "colour + speed + random" shape used by its
///     neighbours in the mode table.
///   - Bloom and Merge have an 8-byte gap: [speed, random, R1, G1, B1, R2, G2, B2],
///     with no direction/reserved byte. Writing a 9th byte here (as Dragonstrike and
///     Crash's direction-bearing two-colour shape does) overflows into the next
///     effect's slice.
///
/// Protocol reconstructed from Gigabyte's own binaries and cross-checked against
/// rcassani/keyboard-fusion-rgb (GPL-3.0). See docs/research/ione-keyboard-protocol.md.
/// </summary>
public static class EffectPacket
{
    /// <summary>Where each effect's configuration slice starts, relative to byte 13.</summary>
    /// <remarks>Frozen so a caller cannot cast it back and corrupt the shared table.</remarks>
    public static IReadOnlyDictionary<LightEffect, int> Offsets { get; } = new Dictionary<LightEffect, int>
    {
        [LightEffect.Static] = 0,
        [LightEffect.Breathing] = 4,
        [LightEffect.Flow] = 9,
        [LightEffect.Firework] = 11,
        [LightEffect.Ripple] = 16,
        [LightEffect.Rain] = 21,
        [LightEffect.Cycling] = 26,
        [LightEffect.Trigger] = 27,
        [LightEffect.Pulse] = 32,
        [LightEffect.Radar] = 37,
        [LightEffect.StarShining] = 43,
        [LightEffect.Wave] = 48,
        [LightEffect.Cross] = 54,
        [LightEffect.Dragonstrike] = 59,
        [LightEffect.Bloom] = 68,
        [LightEffect.Spiral] = 76,
        [LightEffect.Merge] = 78,
        [LightEffect.Crash] = 86,
        [LightEffect.Custom] = 0,
    }.ToFrozenDictionary();

    private static readonly HashSet<LightEffect> NoColor = new()
    {
        LightEffect.Flow, LightEffect.Cycling, LightEffect.Spiral, LightEffect.Custom,
    };

    private static readonly HashSet<LightEffect> TwoColor = new()
    {
        LightEffect.Dragonstrike, LightEffect.Bloom, LightEffect.Merge, LightEffect.Crash,
    };

    private static readonly HashSet<LightEffect> NoSpeed = new()
    {
        LightEffect.Static, LightEffect.Custom,
    };

    private static readonly HashSet<LightEffect> WithDirection = new()
    {
        LightEffect.Flow, LightEffect.Wave, LightEffect.Radar, LightEffect.Spiral,
        LightEffect.Dragonstrike, LightEffect.Crash,
    };

    private static readonly HashSet<LightEffect> WithRandom = new()
    {
        LightEffect.Firework, LightEffect.Rain, LightEffect.Trigger, LightEffect.Pulse,
        LightEffect.Radar, LightEffect.StarShining, LightEffect.Wave, LightEffect.Cross,
        LightEffect.Dragonstrike, LightEffect.Bloom, LightEffect.Merge, LightEffect.Crash,
    };

    public static bool SupportsColor(LightEffect e) => !NoColor.Contains(e);
    public static bool SupportsSecondColor(LightEffect e) => TwoColor.Contains(e);
    public static bool SupportsSpeed(LightEffect e) => !NoSpeed.Contains(e);
    public static bool SupportsDirection(LightEffect e) => WithDirection.Contains(e);
    public static bool SupportsRandom(LightEffect e) => WithRandom.Contains(e);

    /// <summary>UI 0-100 (slow to fast) becomes wire 10-0. The keyboard treats 0 as fastest.</summary>
    public static int EncodeSpeed(int uiPercent) =>
        10 - (int)Math.Round(Math.Clamp(uiPercent, 0, 100) / 10.0, MidpointRounding.AwayFromZero);

    public static byte EncodeDirection(LightEffect effect, LightDirection direction) => direction switch
    {
        LightDirection.Right => 0,
        LightDirection.Left => 1,
        // Gigabyte's own code swaps 2 and 3 for Flow.
        LightDirection.Up => effect == LightEffect.Flow ? (byte)3 : (byte)2,
        LightDirection.Down => effect == LightEffect.Flow ? (byte)2 : (byte)3,
        LightDirection.Clockwise => 0,
        LightDirection.CounterClockwise => 1,
        _ => 0,
    };

    /// <summary>
    /// Builds the 264-byte "read current status" request (command 0x82). The answer comes
    /// back through a separate <c>GetFeature</c> and is the whole shared configuration block.
    /// </summary>
    /// <remarks>
    /// The reference documents only bytes [0] and [1]; bytes 2..263 are zero-filled following
    /// every other documented request, unverified on hardware.
    /// </remarks>
    public static byte[] BuildStatusRequest()
    {
        var request = new byte[KeyboardHid.ReportLength];
        request[0] = KeyboardHid.ReportId;
        request[1] = 0x82;
        return request;
    }

    /// <summary>Builds the 264-byte "set effect" report for these parameters, over a zeroed block.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="p"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The effect id is not one this protocol defines.</exception>
    public static byte[] Build(EffectParameters p) => Build(p, new byte[KeyboardHid.ReportLength]);

    /// <summary>
    /// Patches <paramref name="existing"/> — a block just read back with command 0x82 — with
    /// these parameters, and returns the result as a new array.
    /// </summary>
    /// <remarks>
    /// All 18 effects share one block and each owns a private slice of it, so a write must
    /// carry the other effects' stored configuration back untouched. Sending a freshly zeroed
    /// report would reset every effect the user is not currently looking at. Only the framing
    /// bytes 0..12 and this effect's own slice are written; everything else is copied through.
    /// </remarks>
    /// <param name="p">The effect and its parameters.</param>
    /// <param name="existing">A 264-byte block to patch. Not modified; the caller keeps it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="p"/> or <paramref name="existing"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="existing"/> is not 264 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The effect id is not one this protocol defines.</exception>
    public static byte[] Build(EffectParameters p, byte[] existing)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(existing);
        if (existing.Length != KeyboardHid.ReportLength)
            throw new ArgumentException($"The existing block must be {KeyboardHid.ReportLength} bytes.", nameof(existing));

        var packet = (byte[])existing.Clone();
        Array.Clear(packet, 2, 8); // [2..9] are framing, not storage: zero them even when the read said otherwise
        packet[0] = KeyboardHid.ReportId;
        packet[1] = 0x02;
        packet[10] = (byte)p.Effect;
        packet[11] = p.Effect is LightEffect.Static or LightEffect.StarShining ? (byte)0xFF : (byte)0x00;
        packet[12] = (byte)Math.Clamp(p.BrightnessPercent, 0, 100);

        // These parameters get persisted and read back, so a stale or hand-edited id is a
        // realistic input. It is a programming or persistence error, not a user range, so
        // it throws rather than clamping to some arbitrary effect.
        if (!Offsets.TryGetValue(p.Effect, out var offset))
            throw new ArgumentOutOfRangeException(nameof(p), p.Effect, "Unknown lighting effect id.");

        var at = 13 + offset;
        var speed = (byte)EncodeSpeed(p.SpeedPercent);
        var random = p.Random ? (byte)1 : (byte)0;
        var direction = EncodeDirection(p.Effect, p.Direction);

        switch (p.Effect)
        {
            case LightEffect.Custom:
                break;

            case LightEffect.Static:
                packet[at] = 0x00;
                WriteColor(packet, at + 1, p.Color);
                break;

            case LightEffect.Cycling:
                packet[at] = speed;
                break;

            case LightEffect.Flow:
            case LightEffect.Spiral:
                packet[at] = speed;
                packet[at + 1] = direction;
                break;

            case LightEffect.Wave:
            case LightEffect.Radar:
                // 6-byte slice: speed, random, direction, then colour.
                packet[at] = speed;
                packet[at + 1] = random;
                packet[at + 2] = direction;
                WriteColor(packet, at + 3, p.Color);
                break;

            case LightEffect.Dragonstrike:
            case LightEffect.Crash:
                // 9-byte slice: speed, random, direction, then both colours.
                packet[at] = speed;
                packet[at + 1] = random;
                packet[at + 2] = direction;
                WriteColor(packet, at + 3, p.Color);
                WriteColor(packet, at + 6, p.SecondColor);
                break;

            case LightEffect.Bloom:
            case LightEffect.Merge:
                // 8-byte slice: speed, random, then both colours. No direction byte here -
                // the gap to the next effect's offset is exactly 8, one short of the
                // direction-bearing two-colour shape above.
                packet[at] = speed;
                packet[at + 1] = random;
                WriteColor(packet, at + 2, p.Color);
                WriteColor(packet, at + 5, p.SecondColor);
                break;

            default:
                // Breathing, Firework, Ripple, Rain, Trigger, Pulse, StarShining, Cross:
                // 5-byte slice, speed + random + colour. Breathing and Ripple are the
                // exception: their second byte is an unidentified firmware mode selector,
                // not the random flag. Random travels across effect switches in one
                // EffectParameters record, so it must not leak into that selector here.
                packet[at] = speed;
                // Nor may it be cleared: a byte this driver cannot set is a byte it must carry
                // through untouched, or a read-modify-write destroys the mode the firmware (or
                // Gigabyte's software) had stored. Over the zeroed block the one-argument Build
                // supplies, leaving it alone still yields 0.
                if (SupportsRandom(p.Effect)) packet[at + 1] = random;
                WriteColor(packet, at + 2, p.Color);
                break;
        }

        return packet;
    }

    private static void WriteColor(byte[] packet, int at, RgbColor c)
    {
        packet[at] = c.R;
        packet[at + 1] = c.G;
        packet[at + 2] = c.B;
    }
}
