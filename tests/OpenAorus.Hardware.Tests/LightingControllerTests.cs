using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class LightingControllerTests
{
    private static Task NoDelay(int _) => Task.CompletedTask;

    private static (LightingController Controller, FakeKeyboardHid Hid) Make()
    {
        var hid = new FakeKeyboardHid();
        return (new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUk), NoDelay), hid);
    }

    private static RgbColor[] Solid(RgbColor c) => Enumerable.Repeat(c, KeyLayout.SlotCount).ToArray();

    /// <summary>A 264-byte block whose every byte is non-zero and position-dependent, so a
    /// byte that moves, is cleared or is copied from elsewhere shows up as a mismatch.</summary>
    private static byte[] Recognisable()
    {
        var block = new byte[KeyboardHid.ReportLength];
        for (var i = 0; i < block.Length; i++) block[i] = (byte)(0x80 | (i & 0x3F));
        return block;
    }

    private static void AssertUnchanged(byte[] expected, byte[] actual, int from, int to)
    {
        for (var i = from; i < to; i++)
            Assert.True(expected[i] == actual[i], $"byte {i}: expected 0x{expected[i]:X2}, got 0x{actual[i]:X2}");
    }

    [Fact]
    public async Task ApplyEffect_reads_the_stored_block_then_writes_the_patched_report()
    {
        var (ctl, hid) = Make();
        hid.Responses.Enqueue(new byte[KeyboardHid.ReportLength]);
        var p = EffectParameters.Default(LightEffect.Wave) with { Color = new RgbColor(9, 8, 7) };

        var result = await ctl.ApplyEffectAsync(p);

        Assert.True(result.Success);
        Assert.Equal(2, hid.Written.Count);
        Assert.Equal(0x82, hid.Command(0));
        Assert.Equal(0x02, hid.Command(1));
        Assert.Equal(EffectPacket.Build(p), hid.Written[^1]);
    }

    [Fact]
    public async Task ApplyEffect_preserves_every_byte_the_effect_does_not_own()
    {
        var (ctl, hid) = Make();
        var stored = Recognisable();
        hid.Responses.Enqueue(stored);
        var p = EffectParameters.Default(LightEffect.Wave) with { BrightnessPercent = 42 };

        Assert.True((await ctl.ApplyEffectAsync(p)).Success);

        var written = hid.Written[^1];
        Assert.Equal(KeyboardHid.ReportId, written[0]);
        Assert.Equal(0x02, written[1]);
        AssertUnchanged(new byte[KeyboardHid.ReportLength], written, 2, 10); // [2..9] framing zeroes
        Assert.Equal((byte)LightEffect.Wave, written[10]);
        Assert.Equal(0x00, written[11]);
        Assert.Equal(42, written[12]);

        // Wave owns 13 + 48 .. +6. Everything on either side of that slice is the keyboard's.
        AssertUnchanged(stored, written, 13, 13 + 48);
        AssertUnchanged(stored, written, 13 + 48 + 6, KeyboardHid.ReportLength);
        Assert.NotEqual(stored[13 + 48], written[13 + 48]); // and the slice really was rewritten
    }

    [Fact]
    public async Task ApplyEffect_falls_back_to_zeroes_when_the_keyboard_does_not_answer()
    {
        var (ctl, hid) = Make();
        var p = EffectParameters.Default(LightEffect.Breathing);

        var result = await ctl.ApplyEffectAsync(p);

        Assert.True(result.Success);
        Assert.Equal(EffectPacket.Build(p), hid.Written[^1]);
    }

    [Fact]
    public async Task ApplyEffect_falls_back_to_zeroes_when_the_answer_is_the_wrong_length()
    {
        var (ctl, hid) = Make();
        hid.Responses.Enqueue(new byte[] { 0x07, 0x82, 0xFF, 0xFF });
        var p = EffectParameters.Default(LightEffect.Ripple);

        var result = await ctl.ApplyEffectAsync(p);

        Assert.True(result.Success);
        Assert.Equal(EffectPacket.Build(p), hid.Written[^1]);
    }

    [Fact]
    public async Task ApplyEffect_still_succeeds_when_the_status_request_is_rejected()
    {
        var (ctl, hid) = Make();
        hid.FailWriteAt = 0;
        hid.Responses.Enqueue(Recognisable()); // must not be consumed: the request never landed
        var p = EffectParameters.Default(LightEffect.Rain);

        var result = await ctl.ApplyEffectAsync(p);

        Assert.True(result.Success);
        Assert.Equal(EffectPacket.Build(p), hid.Written[^1]);
        Assert.Single(hid.Responses);
    }

    [Fact]
    public async Task ApplyEffect_clears_the_marker_byte_when_the_new_effect_does_not_use_it()
    {
        var (ctl, hid) = Make();
        var stored = new byte[KeyboardHid.ReportLength];
        stored[10] = (byte)LightEffect.Static;
        stored[11] = 0xFF;
        hid.Responses.Enqueue(stored);

        Assert.True((await ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Wave))).Success);

        Assert.Equal(0x00, hid.Written[^1][11]);
    }

    [Fact]
    public async Task ApplyEffect_sets_the_marker_byte_for_Static()
    {
        var (ctl, hid) = Make();
        hid.Responses.Enqueue(new byte[KeyboardHid.ReportLength]);

        Assert.True((await ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static))).Success);

        Assert.Equal(0xFF, hid.Written[^1][11]);
    }

    [Fact]
    public async Task ApplyEffect_reports_a_rejected_effect_report()
    {
        var (ctl, hid) = Make();
        hid.FailWriteAt = 1;

        var result = await ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("Static", result.Error);
    }

    [Fact]
    public async Task ApplyEffect_reports_an_unknown_effect_id_instead_of_throwing()
    {
        var (ctl, hid) = Make();
        var p = EffectParameters.Default((LightEffect)0x40);

        var result = await ctl.ApplyEffectAsync(p);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain(hid.Written, w => w[1] == 0x02);
    }

    [Fact]
    public async Task ApplyPerKey_writes_both_colour_reports_before_selecting_custom_mode()
    {
        var (ctl, hid) = Make();
        var colors = Solid(new RgbColor(0x10, 0x20, 0x30));

        var result = await ctl.ApplyPerKeyAsync(colors, brightnessPercent: 70);

        Assert.True(result.Success);
        Assert.Equal(4, hid.Written.Count);
        Assert.Equal(0x06, hid.Command(0));
        Assert.Equal(1, hid.Written[0][3]);
        Assert.Equal(0x06, hid.Command(1));
        Assert.Equal(2, hid.Written[1][3]);
        Assert.Equal(0x82, hid.Command(2)); // read-modify-write of the shared block
        Assert.Equal(0x02, hid.Command(3));
        Assert.Equal((byte)LightEffect.Custom, hid.Written[3][10]);
        Assert.Equal(70, hid.Written[3][12]);
    }

    [Fact]
    public async Task ApplyPerKey_preserves_the_other_effects_configuration()
    {
        var (ctl, hid) = Make();
        var stored = Recognisable();
        hid.Responses.Enqueue(stored);

        Assert.True((await ctl.ApplyPerKeyAsync(Solid(RgbColor.White), 55)).Success);

        // Custom owns no configuration bytes at all, so everything past the header survives.
        AssertUnchanged(stored, hid.Written[^1], 13, KeyboardHid.ReportLength);
    }

    [Fact]
    public async Task ApplyPerKey_stops_and_reports_when_the_first_report_fails()
    {
        var (ctl, hid) = Make();
        hid.FailNextWrite = true;

        var result = await ctl.ApplyPerKeyAsync(Solid(RgbColor.White), 50);

        Assert.False(result.Success);
        Assert.Contains("colour", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(hid.Written);
    }

    [Fact]
    public async Task ApplyPerKey_reports_a_rejected_mode_selection_separately()
    {
        var (ctl, hid) = Make();
        hid.FailWriteAt = 3;

        var result = await ctl.ApplyPerKeyAsync(Solid(RgbColor.White), 50);

        Assert.False(result.Success);
        Assert.Contains("custom", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyPerKey_reports_a_wrong_sized_colour_list_instead_of_throwing()
    {
        var (ctl, hid) = Make();

        var result = await ctl.ApplyPerKeyAsync(new[] { RgbColor.White }, 50);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(hid.Written);
    }

    [Fact]
    public async Task ReadPerKey_round_trips_through_the_two_page_responses()
    {
        var (ctl, hid) = Make();
        var colors = Solid(new RgbColor(1, 2, 3));
        var (first, second) = PerKeyPacket.BuildWrite(colors);
        hid.Responses.Enqueue(first);
        hid.Responses.Enqueue(second);

        var read = await ctl.ReadPerKeyAsync();

        Assert.Equal(colors, read);
        Assert.Equal(2, hid.Written.Count);
        Assert.Equal(0x86, hid.Command(0));
        Assert.Equal(0x86, hid.Command(1));
    }

    /// <summary>
    /// <see cref="PerKeyPacket.Parse"/> ignores response headers, so nothing inside it can
    /// notice the two pages arriving the wrong way round; the controller is the only place
    /// the pairing is observable. The two fake responses carry a different marker in every
    /// plane, so a swap produces different colours rather than the same ones.
    /// </summary>
    [Fact]
    public async Task ReadPerKey_pairs_each_page_request_with_its_own_response()
    {
        var (ctl, hid) = Make();
        hid.Responses.Enqueue(Plane(0x10, 0x20)); // page 1: reds 0x1x, greens 0x2x
        hid.Responses.Enqueue(Plane(0x30, 0x40)); // page 2: blues 0x3x, then padding 0x4x

        var read = await ctl.ReadPerKeyAsync();

        Assert.Equal(1, hid.Written[0][3]);
        Assert.Equal(2, hid.Written[1][3]);
        Assert.NotNull(read);
        for (var i = 0; i < KeyLayout.SlotCount; i++)
        {
            var low = i & 0x0F;
            Assert.Equal(new RgbColor((byte)(0x10 | low), (byte)(0x20 | low), (byte)(0x30 | low)), read[i]);
        }

        static byte[] Plane(byte firstHalf, byte secondHalf)
        {
            var report = new byte[KeyboardHid.ReportLength];
            report[0] = KeyboardHid.ReportId;
            for (var i = 0; i < KeyLayout.SlotCount; i++)
            {
                report[PerKeyPacket.HeaderLength + i] = (byte)(firstHalf | (i & 0x0F));
                report[PerKeyPacket.HeaderLength + KeyLayout.SlotCount + i] = (byte)(secondHalf | (i & 0x0F));
            }
            return report;
        }
    }

    [Fact]
    public async Task ReadPerKey_returns_null_when_the_keyboard_does_not_answer()
    {
        var (ctl, _) = Make();
        Assert.Null(await ctl.ReadPerKeyAsync());
    }

    [Fact]
    public async Task ReadPerKey_returns_null_when_only_the_first_page_answers()
    {
        var (ctl, hid) = Make();
        hid.Responses.Enqueue(new byte[KeyboardHid.ReportLength]);

        Assert.Null(await ctl.ReadPerKeyAsync());
    }

    [Fact]
    public async Task SetBrightness_resends_the_current_effect_with_the_new_value()
    {
        var (ctl, hid) = Make();
        var current = EffectParameters.Default(LightEffect.Breathing) with { BrightnessPercent = 20 };

        var result = await ctl.SetBrightnessAsync(current, 90);

        Assert.True(result.Success);
        Assert.Equal(0x02, hid.Command(hid.Written.Count - 1));
        Assert.Equal((byte)LightEffect.Breathing, hid.Written[^1][10]);
        Assert.Equal(90, hid.Written[^1][12]);
    }

    [Fact]
    public async Task An_absent_keyboard_fails_every_call_without_writing()
    {
        var hid = new FakeKeyboardHid { IsPresent = false, Identity = null };
        var ctl = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUs), NoDelay);

        Assert.False((await ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static))).Success);
        Assert.False((await ctl.ApplyPerKeyAsync(Solid(RgbColor.White), 50)).Success);
        Assert.False((await ctl.SetBrightnessAsync(EffectParameters.Default(LightEffect.Static), 10)).Success);
        Assert.Null(await ctl.ReadPerKeyAsync());
        Assert.Empty(hid.Written);
        Assert.False(ctl.IsPresent);
        Assert.Equal(KeyboardLayout.EngUs, ctl.Layout.Layout);
    }

    [Fact]
    public async Task Writes_are_paced_between_reports_but_not_after_the_last()
    {
        var delays = 0;
        var hid = new FakeKeyboardHid();
        var ctl = new LightingController(
            hid, KeyLayout.For(KeyboardLayout.EngUk), ms => { Assert.Equal(LightingController.WriteDelayMs, ms); delays++; return Task.CompletedTask; });

        await ctl.ApplyPerKeyAsync(Solid(RgbColor.White), 50);

        Assert.Equal(4, hid.Written.Count);
        Assert.Equal(3, delays);
    }

    /// <summary>
    /// The per-key path already covers pacing across four reports, but selecting an effect is
    /// the operation a brightness slider fires over and over, and its own two reports have to
    /// be spaced apart or the keyboard drops one.
    /// </summary>
    [Fact]
    public async Task An_effect_apply_paces_its_status_read_and_its_write()
    {
        var delays = 0;
        var hid = new FakeKeyboardHid();
        var ctl = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUk), _ => { delays++; return Task.CompletedTask; });

        await ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static));

        Assert.Equal(2, hid.Written.Count);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task Concurrent_applies_are_serialized()
    {
        var hid = new FakeKeyboardHid();
        var gate = new SemaphoreSlim(0);
        var ctl = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUk), async _ => await gate.WaitAsync());

        var first = ctl.ApplyPerKeyAsync(Solid(RgbColor.Black), 10);
        var second = ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static));

        Assert.Single(hid.Written);
        gate.Release(8);
        await first;
        await second;

        Assert.Equal(6, hid.Written.Count);
        Assert.Equal(0x02, hid.Command(3)); // the per-key apply finished before the effect apply began
        Assert.Equal((byte)LightEffect.Custom, hid.Written[3][10]);
        Assert.Equal((byte)LightEffect.Static, hid.Written[^1][10]);
    }

    // ---- Cancellation ---------------------------------------------------------------
    // Cancelling throws rather than returning a failed result, matching FanController: a
    // caller that cancelled asked for the sequence to stop and has no error to show anyone.
    // What matters is where it stops - between reports, never mid-report - and that the gate
    // comes back, so the next apply is not stuck behind an abandoned one.

    [Fact]
    public async Task A_cancelled_token_stops_the_sequence_between_reports_and_frees_the_gate()
    {
        var hid = new FakeKeyboardHid();
        var cts = new CancellationTokenSource();
        // Cancel while the pacing delay after the first colour report is running.
        var ctl = new LightingController(
            hid, KeyLayout.For(KeyboardLayout.EngUk), _ => { cts.Cancel(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ctl.ApplyPerKeyAsync(Solid(RgbColor.White), 50, cts.Token));

        // It stopped between reports, not mid-report: only page one went out, and the
        // keyboard was never switched to Custom, so no half-painted state is on screen.
        Assert.Single(hid.Written);
        Assert.Equal(0x06, hid.Command(0));

        // The gate is free: the next apply is not deadlocked behind the cancelled one.
        Assert.True((await ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static))).Success);
    }

    /// <summary>
    /// The check between the colour pages and the mode selection. Cancelling on the second
    /// pause means the colours are written but the keyboard has not been switched to them;
    /// without the check the selection goes ahead anyway and a cancelled apply still puts a
    /// 0x82 status read - and then the mode change behind it - on the wire.
    /// </summary>
    [Fact]
    public async Task Cancelling_after_the_colour_pages_stops_before_custom_is_selected()
    {
        var hid = new FakeKeyboardHid();
        var cts = new CancellationTokenSource();
        var pauses = 0;
        var ctl = new LightingController(
            hid, KeyLayout.For(KeyboardLayout.EngUk), _ => { if (++pauses == 2) cts.Cancel(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ctl.ApplyPerKeyAsync(Solid(RgbColor.White), 50, cts.Token));

        Assert.Equal(2, hid.Written.Count);
        Assert.Equal(0x06, hid.Command(0));
        Assert.Equal(0x06, hid.Command(1));
    }

    /// <summary>
    /// The same check inside the effect selection itself, which is the one a brightness slider
    /// cancels when the window closes under it: the status read has gone out, and the write
    /// that would change the lighting must not follow it.
    /// </summary>
    [Fact]
    public async Task Cancelling_an_effect_apply_stops_after_the_status_read()
    {
        var hid = new FakeKeyboardHid();
        var cts = new CancellationTokenSource();
        var ctl = new LightingController(
            hid, KeyLayout.For(KeyboardLayout.EngUk), _ => { cts.Cancel(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static), cts.Token));

        var only = Assert.Single(hid.Written);
        Assert.Equal(0x82, only[1]);
    }

    [Fact]
    public async Task An_already_cancelled_token_writes_nothing()
    {
        var (ctl, hid) = Make();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static), cts.Token));

        Assert.Empty(hid.Written);
    }

    [Fact]
    public async Task A_cancelled_per_key_read_frees_the_gate()
    {
        var hid = new FakeKeyboardHid();
        hid.Responses.Enqueue(new byte[KeyboardHid.ReportLength]);
        var cts = new CancellationTokenSource();
        var ctl = new LightingController(
            hid, KeyLayout.For(KeyboardLayout.EngUk), _ => { cts.Cancel(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctl.ReadPerKeyAsync(cts.Token));

        Assert.True((await ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static))).Success);
    }
}
