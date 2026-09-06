namespace OpenAorus.Hardware.Lighting;

/// <summary>The outcome of one lighting sequence: either it landed, or it says why not.</summary>
public sealed record LightingResult(bool Success, string? Error)
{
    /// <summary>The sequence completed.</summary>
    public static LightingResult Ok() => new(true, null);

    /// <summary>The sequence stopped; <paramref name="error"/> is fit to show the owner.</summary>
    public static LightingResult Fail(string error) => new(false, error);
}

/// <summary>
/// Sequences lighting writes. Reports are serialized and paced <see cref="WriteDelayMs"/>
/// apart, matching Gigabyte's own software: the keyboard silently drops reports sent faster.
///
/// Selecting an effect is a read-modify-write, not a plain write. All 19 effects share one
/// 264-byte block, and the 18 that carry configuration each own a slice of it (Custom keeps
/// its colours elsewhere and owns none), so writing a freshly zeroed report would reset
/// every effect except the one being selected. The block is read back with command
/// 0x82 first and only the active effect's slice is patched. A read that fails or answers
/// with the wrong length falls back to zeroes, which is no worse than not reading at all.
/// </summary>
public sealed class LightingController
{
    /// <summary>Pause between consecutive feature reports.</summary>
    public const int WriteDelayMs = 65;

    private readonly IKeyboardHid _hid;
    private readonly Func<int, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The key-slot map the per-key editor paints onto.</summary>
    public KeyLayout Layout { get; }

    /// <summary>Whether a supported lighting collection is open.</summary>
    public bool IsPresent => _hid.IsPresent;

    /// <param name="hid">The lighting collection.</param>
    /// <param name="layout">The 128-slot map for this keyboard.</param>
    /// <param name="delay">Pacing hook; defaults to <see cref="Task.Delay(int)"/>. Tests pass a no-op.</param>
    public LightingController(IKeyboardHid hid, KeyLayout layout, Func<int, Task>? delay = null)
    {
        _hid = hid;
        Layout = layout;
        _delay = delay ?? (ms => Task.Delay(ms));
    }

    /// <summary>Selects an effect and its parameters, preserving every other effect's stored configuration.</summary>
    public async Task<LightingResult> ApplyEffectAsync(EffectParameters p, CancellationToken ct = default)
    {
        if (!_hid.IsPresent) return LightingResult.Fail("No supported keyboard found.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SelectEffectAsync(p, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Writes 128 custom colours and switches the keyboard to them.
    /// </summary>
    /// <remarks>
    /// The colours go first and the mode selection last: selecting Custom before the new
    /// colours arrive shows whatever colours the keyboard still held, for as long as the
    /// two writes take.
    /// </remarks>
    /// <param name="colors">Exactly <see cref="KeyLayout.SlotCount"/> colours, in slot order.</param>
    /// <param name="brightnessPercent">0-100.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<LightingResult> ApplyPerKeyAsync(IReadOnlyList<RgbColor> colors, int brightnessPercent, CancellationToken ct = default)
    {
        if (!_hid.IsPresent) return LightingResult.Fail("No supported keyboard found.");

        byte[] first, second;
        try { (first, second) = PerKeyPacket.BuildWrite(colors); }
        catch (ArgumentException ex) { return LightingResult.Fail(ex.Message); }

        var select = EffectParameters.Default(LightEffect.Custom) with { BrightnessPercent = brightnessPercent };

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_hid.SetFeature(first)) return LightingResult.Fail("The keyboard rejected the first per-key colour report.");
            await _delay(WriteDelayMs).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (!_hid.SetFeature(second)) return LightingResult.Fail("The keyboard rejected the second per-key colour report.");
            await _delay(WriteDelayMs).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            var selected = await SelectEffectAsync(select, ct).ConfigureAwait(false);
            return selected.Success
                ? selected
                : LightingResult.Fail("The colours were written but switching to custom mode failed.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Reads the 128 colours the keyboard currently holds, or null if it does not answer.
    /// </summary>
    public async Task<RgbColor[]?> ReadPerKeyAsync(CancellationToken ct = default)
    {
        if (!_hid.IsPresent) return null;

        var (firstRequest, secondRequest) = PerKeyPacket.BuildRead();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Each request is answered before the next is sent, so the two responses stay
            // paired with their pages: Parse cannot tell them apart on its own.
            if (!_hid.SetFeature(firstRequest)) return null;
            var firstResponse = _hid.GetFeature();
            if (firstResponse is not { Length: KeyboardHid.ReportLength }) return null;
            await _delay(WriteDelayMs).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (!_hid.SetFeature(secondRequest)) return null;
            var secondResponse = _hid.GetFeature();
            if (secondResponse is not { Length: KeyboardHid.ReportLength }) return null;

            return PerKeyPacket.Parse(firstResponse, secondResponse);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Changes brightness by re-sending the current effect: the protocol has no standalone
    /// brightness command.
    /// </summary>
    public Task<LightingResult> SetBrightnessAsync(EffectParameters current, int brightnessPercent, CancellationToken ct = default) =>
        ApplyEffectAsync(current with { BrightnessPercent = brightnessPercent }, ct);

    /// <summary>Read-modify-write of the shared block. The caller already holds the gate.</summary>
    private async Task<LightingResult> SelectEffectAsync(EffectParameters p, CancellationToken ct)
    {
        var stored = ReadStoredBlock();
        await _delay(WriteDelayMs).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        byte[] report;
        // These parameters are persisted between runs, so a stale effect id can reach here.
        // EffectPacket throws on one; the owner gets a message rather than a crashed handler.
        try { report = EffectPacket.Build(p, stored); }
        catch (ArgumentException ex) { return LightingResult.Fail(ex.Message); }

        return _hid.SetFeature(report)
            ? LightingResult.Ok()
            : LightingResult.Fail($"The keyboard rejected the {p.Effect} effect report.");
    }

    /// <summary>
    /// Asks for the shared configuration block. Anything other than a 264-byte answer becomes
    /// zeroes: losing the other effects' settings is the pre-existing behaviour, and is a far
    /// better outcome than refusing to change the lighting at all.
    /// </summary>
    private byte[] ReadStoredBlock()
    {
        if (!_hid.SetFeature(EffectPacket.BuildStatusRequest())) return new byte[KeyboardHid.ReportLength];
        var response = _hid.GetFeature();
        return response is { Length: KeyboardHid.ReportLength } ? response : new byte[KeyboardHid.ReportLength];
    }
}
