using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>One keypress, one action, one overlay at most.</summary>
/// <remarks>
/// The window is a chosen number, not a measured one, so these tests pin behaviour rather than
/// timing: what a held key costs, what two real taps must still do, and what a clock that misbehaves
/// must not be able to do. VERIFY 7.4 is what measures the number itself.
/// </remarks>
public class SignalDebouncerTests
{
    [Fact]
    public void The_same_signal_twice_inside_the_window_fires_once()
    {
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeStealth);

        Assert.True(d.ShouldFire(e, 1000));
        Assert.False(d.ShouldFire(e, 1000 + SignalDebouncer.WindowMs - 1));
    }

    [Fact]
    public void The_same_signal_outside_the_window_fires_twice()
    {
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeStealth);

        Assert.True(d.ShouldFire(e, 1000));
        Assert.True(d.ShouldFire(e, 1000 + SignalDebouncer.WindowMs));
    }

    [Fact]
    public void Different_signals_never_suppress_each_other()
    {
        var d = new SignalDebouncer();

        Assert.True(d.ShouldFire(new HotkeyEvent(HotkeySignal.FanModeStealth), 1000));
        Assert.True(d.ShouldFire(new HotkeyEvent(HotkeySignal.WifiEnabled), 1000));
        Assert.True(d.ShouldFire(new HotkeyEvent(HotkeySignal.TouchpadDisabled), 1000));
    }

    [Fact]
    public void Two_quick_backlight_taps_are_two_events_because_the_level_is_part_of_the_key()
    {
        // The reason the key is not the signal alone: 0 % then 50 % inside the window is the
        // owner tapping the key twice, and dropping the second would leave the slider behind.
        var d = new SignalDebouncer();

        Assert.True(d.ShouldFire(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 0), 1000));
        Assert.True(d.ShouldFire(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 50), 1050));
        Assert.False(d.ShouldFire(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 50), 1100));
    }

    [Fact]
    public void A_repeat_of_the_same_level_much_later_fires_again()
    {
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 50);

        Assert.True(d.ShouldFire(e, 1000));
        Assert.False(d.ShouldFire(e, 1100));
        Assert.True(d.ShouldFire(e, 5000));
    }

    [Fact]
    public void A_held_key_repeating_is_suppressed_for_as_long_as_it_repeats()
    {
        // Auto-repeat is the duplication most likely to be real. One fan sequence per repeat would
        // be seconds of controller traffic for one finger.
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeAutoHigh);

        Assert.True(d.ShouldFire(e, 0));
        for (var t = 33; t < SignalDebouncer.WindowMs; t += 33)
            Assert.False(d.ShouldFire(e, t));
    }

    [Fact]
    public void A_key_held_across_one_window_produces_exactly_one_action()
    {
        // Windows repeats a held key at up to about 30 a second. Nine reports, one fan write.
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeAutoHigh);

        var fired = 0;
        for (var t = 0; t < SignalDebouncer.WindowMs; t += 30)
            if (d.ShouldFire(e, t)) fired++;

        Assert.Equal(1, fired);
    }

    [Fact]
    public void A_longer_hold_fires_again_because_this_throttles_rather_than_latches()
    {
        // Worth pinning because it is the limit of what this class can do: there is no key-up on
        // the vendor collections, so a hold is indistinguishable from a key pressed over and over.
        // The window caps the rate at roughly one action per window - twelve fan writes for a
        // three-second hold rather than a hundred - and does not hold anything off until release.
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeAutoHigh);

        var reports = 0;
        var fired = 0;
        for (var t = 0; t < 3000; t += 30)
        {
            reports++;
            if (d.ShouldFire(e, t)) fired++;
        }

        Assert.Equal(100, reports);
        Assert.Equal(12, fired); // one per window, phased by the 30 ms repeat: 0, 270, 540, ...
    }

    [Fact]
    public void A_shorter_window_can_be_asked_for()
    {
        var d = new SignalDebouncer(windowMs: 10);
        var e = new HotkeyEvent(HotkeySignal.WifiDisabled);

        Assert.True(d.ShouldFire(e, 0));
        Assert.False(d.ShouldFire(e, 5));
        Assert.True(d.ShouldFire(e, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_window_of_zero_or_less_suppresses_nothing(int windowMs)
    {
        // Not an error worth throwing over: a caller who asks for no debouncing gets no
        // debouncing, which is the same behaviour as not having one of these at all.
        var d = new SignalDebouncer(windowMs);
        var e = new HotkeyEvent(HotkeySignal.FanModeStealth);

        Assert.True(d.ShouldFire(e, 1000));
        Assert.True(d.ShouldFire(e, 1000));
        Assert.True(d.ShouldFire(e, 1001));
    }

    [Fact]
    public void A_timestamp_that_goes_backwards_does_not_wedge_it_shut()
    {
        // Nothing should hand it one, but a clock that jumped must not leave a signal suppressed
        // for as long as the process runs.
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeStealth);

        Assert.True(d.ShouldFire(e, 10_000));
        Assert.True(d.ShouldFire(e, 5_000));
    }

    [Fact]
    public void After_the_clock_jumps_back_the_window_restarts_from_where_it_landed()
    {
        // The jump is not just tolerated, it re-anchors: the next window is measured from the
        // press that survived the jump, so debouncing carries on working afterwards.
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeStealth);

        Assert.True(d.ShouldFire(e, 10_000));
        Assert.True(d.ShouldFire(e, 5_000));
        Assert.False(d.ShouldFire(e, 5_100));
        Assert.True(d.ShouldFire(e, 5_000 + SignalDebouncer.WindowMs));
    }

    [Fact]
    public void A_gap_too_large_for_a_long_is_not_wrapped_into_a_suppression()
    {
        // The whole 64-bit range in one step: the subtraction overflows and comes back negative,
        // and a naive "less than the window" would read that as "no time has passed" and suppress
        // every press from then on - the wedged-shut failure, reached going forwards.
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeStealth);

        Assert.True(d.ShouldFire(e, long.MinValue));
        Assert.True(d.ShouldFire(e, long.MaxValue));
        Assert.False(d.ShouldFire(e, long.MaxValue));
    }

    [Fact]
    public void Every_brightness_byte_is_its_own_key()
    {
        // The reason the table needs no eviction: what the decoders can produce is thirteen
        // signals, three backlight steps and 256 brightness bytes. It cannot grow past that.
        var d = new SignalDebouncer();

        for (var level = 0; level <= 255; level++)
            Assert.True(d.ShouldFire(new HotkeyEvent(HotkeySignal.DisplayBrightness, level), 1000));

        Assert.False(d.ShouldFire(new HotkeyEvent(HotkeySignal.DisplayBrightness, 128), 1100));
    }

    [Fact]
    public void Two_channels_calling_at_once_neither_lose_a_press_nor_corrupt_the_table()
    {
        // Raw input arrives on the message-only window's thread and WMI events on a thread-pool
        // thread, and both feed one of these. The first phase uses far more keys than the decoders
        // can produce, on purpose: growing the table is where an unsynchronised Dictionary throws
        // or silently drops entries, and it would do it on a callback thread where the exception
        // stops the channel and nobody ever sees it.
        const int threads = 8;
        const int perThread = 5000;
        var d = new SignalDebouncer();
        var distinctFired = 0;
        var sharedFired = 0;

        Parallel.For(0, threads, t =>
        {
            var mine = 0;
            for (var i = 0; i < perThread; i++)
                if (d.ShouldFire(new HotkeyEvent(HotkeySignal.DisplayBrightness, (t * perThread) + i), 1000))
                    mine++;
            Interlocked.Add(ref distinctFired, mine);
        });

        Assert.Equal(threads * perThread, distinctFired);

        // And the answer they agree on for one shared key is still one press, one action.
        var shared = new HotkeyEvent(HotkeySignal.FanModeStealth);
        Parallel.For(0, threads, _ =>
        {
            var mine = 0;
            for (var i = 0; i < perThread; i++)
                if (d.ShouldFire(shared, 1000)) mine++;
            Interlocked.Add(ref sharedFired, mine);
        });

        Assert.Equal(1, sharedFired);
    }

    [Fact]
    public void Nothing_a_decoder_can_hand_it_makes_it_throw()
    {
        // This sits on the path from a device, behind two decoders that are themselves total.
        var d = new SignalDebouncer();
        var times = new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue, long.MinValue, 0L };

        foreach (var signal in Enum.GetValues<HotkeySignal>())
            foreach (var level in new[] { int.MinValue, -1, 0, 100, 255, int.MaxValue })
                foreach (var now in times)
                    Assert.Null(Record.Exception(() => { d.ShouldFire(new HotkeyEvent(signal, level), now); }));
    }

    [Fact]
    public void A_null_signal_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => new SignalDebouncer().ShouldFire(null!, 0));
    }

    [Fact]
    public void The_window_is_the_chosen_quarter_second()
    {
        // Pinned so that changing it is a decision rather than a drive-by. It sits above the gap
        // between two channels describing one press and below a deliberate double tap; neither
        // bound has been measured, which is what VERIFY 7.4 is for.
        Assert.Equal(250, SignalDebouncer.WindowMs);
    }
}
