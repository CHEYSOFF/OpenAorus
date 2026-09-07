namespace OpenAorus.Hardware.Hotkeys;

/// <summary>
/// Decides, one signal at a time, whether it is a fresh keypress or a repeat of one already acted on.
/// </summary>
/// <remarks>
/// <para>
/// The design spec justifies this as cross-channel echo - the two channels describing one physical
/// press. On the documented data their vocabularies do not overlap at all: the fan, backlight and
/// brightness reports arrive on raw input and only touchpad and Wi-Fi arrive on WMI, so no signal
/// can reach both decoders and that duplication has never been observed. This is insurance
/// against it.
/// </para>
/// <para>
/// The duplication that is certainly real is key auto-repeat. A held key repeats at up to about
/// thirty a second, and one fan-mode change is five to seven WMI writes separated by
/// <see cref="Fans.FanController.StepDelayMs"/>, so an undebounced hold queues seconds of
/// controller traffic per repeat and the machine spends the next minute working through them.
/// That is the failure this class exists to prevent.
/// </para>
/// <para>
/// The key is the signal <em>and</em> its level. A level-blind key would treat two quick backlight
/// taps as one press and leave the slider showing the level the keyboard had already moved past.
/// </para>
/// <para>
/// It throttles rather than latches: there is no key-up on the vendor collections, so a hold that
/// outlasts the window fires again. The window bounds the rate, it does not hold anything off
/// until release.
/// </para>
/// <para>
/// The table is bounded by what the decoders can produce - thirteen signals, three backlight steps
/// and 256 brightness bytes - so it cannot grow without limit and there is nothing to evict.
/// </para>
/// </remarks>
public sealed class SignalDebouncer
{
    /// <summary>How long one signal-and-level suppresses a repeat of itself, in milliseconds.</summary>
    /// <remarks>Chosen to sit above the gap between two channels describing one press and below a
    /// deliberate double tap. Neither bound has been measured - VERIFY 7.4, five rapid presses, is
    /// what measures them.</remarks>
    public const int WindowMs = 250;

    private readonly Dictionary<(HotkeySignal Signal, int Level), long> _lastFired = new();
    private readonly int _windowMs;

    /// <summary>Creates a debouncer.</summary>
    /// <param name="windowMs">The suppression window. Defaults to <see cref="WindowMs"/>; zero or
    /// less suppresses nothing, which is the same behaviour as having no debouncer at all.</param>
    public SignalDebouncer(int windowMs = WindowMs) => _windowMs = windowMs;

    /// <summary>Feeds one decoded signal in and answers whether it should be acted on.</summary>
    /// <param name="signal">The decoded signal.</param>
    /// <param name="nowMs">A monotonic millisecond timestamp; the app passes
    /// <see cref="Environment.TickCount64"/>.</param>
    /// <returns>True for a fresh press, false for a repeat inside the window.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signal"/> is null.</exception>
    /// <remarks>Safe to call from more than one thread. The two channels run on different ones -
    /// raw input on the message-only window's thread, WMI on a thread-pool callback - and share
    /// one of these, and an unsynchronised table could be corrupted or made to throw there, where
    /// the exception would stop the channel and never be seen.</remarks>
    public bool ShouldFire(HotkeyEvent signal, long nowMs)
    {
        // Null is this app calling itself wrongly, not something a device can send: a decoder that
        // did not recognise its input returns null and the caller is meant to stop there.
        ArgumentNullException.ThrowIfNull(signal);

        var key = (signal.Signal, signal.Level);

        lock (_lastFired)
        {
            if (_lastFired.TryGetValue(key, out var last) && IsInsideWindow(last, nowMs))
                return false;

            // A suppressed repeat deliberately does not extend the window, so a key held down
            // fires once per window rather than never firing again until it is released.
            _lastFired[key] = nowMs;
            return true;
        }
    }

    private bool IsInsideWindow(long last, long nowMs)
    {
        // Both halves are about a clock that misbehaves, and both err towards firing: a signal
        // that is wrongly acted on twice is a duplicate overlay, while one that is wrongly
        // suppressed is a key that stops working for as long as the process runs.
        //
        // nowMs < last is a clock that jumped or wrapped backwards. That press fires and re-anchors
        // the window where it landed, rather than waiting out a window that ends in the future.
        //
        // elapsed < 0 with nowMs >= last is the same jump going forwards, far enough that the
        // subtraction overflows: the wrapped difference is always negative, and reading it as a
        // small number of milliseconds would suppress every press from then on.
        var elapsed = nowMs - last;
        return nowMs >= last && elapsed >= 0 && elapsed < _windowMs;
    }
}
