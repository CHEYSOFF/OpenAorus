using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.App.Hotkeys;

/// <summary>
/// Wires the two channels to the pure parts and raises one action per real keypress.
/// </summary>
/// <remarks>
/// <para>
/// Wiring and nothing else: source, decoder, debouncer, policy, event. Every decision lives in
/// <see cref="HotkeyPolicy"/> and every byte pattern in <see cref="RawInputDecoder"/>, so this
/// class has nothing to get wrong that a test could not see.
/// </para>
/// <para>
/// It raises rather than acts, for the reason <see cref="FanWatchdog"/> does: the
/// acting needs the fan controller, the lighting view model and a window, and none of those
/// belong on the path a raw-input message travels.
/// </para>
/// <para>
/// THE NULL JOIN. Both decoders return null for input they do not recognise and
/// <see cref="HotkeyPolicy.Decide"/> throws on a null signal. <see cref="Handle"/> is the one
/// place those two meet, and the check there is not a defensive habit: these reports arrive under
/// <c>RIDEV_INPUTSINK</c> regardless of focus, so without it every unrecognised report on the
/// machine - a report from another vendor collection, a length this app has never seen - becomes
/// an unhandled exception on a window procedure.
/// </para>
/// <para>
/// TWO THREADS, ONE SERVICE. Raw input is delivered on the message-only window's thread and WMI
/// events on a thread-pool callback, so everything reachable from <see cref="Handle"/> has to be
/// safe on either: <see cref="SignalDebouncer"/> locks its own table, the settings and the mode
/// are reads, and <see cref="HotkeyPolicy"/> is pure. What is not safe on either is everything
/// downstream - view-model properties and a window - so the action is handed to the UI through
/// <c>post</c> rather than raised on whichever thread the key arrived on.
/// </para>
/// <para>
/// The settings object is the live one, not a copy. Toggling hotkeys off in the Settings window
/// takes effect on the next keypress instead of on the next launch.
/// </para>
/// </remarks>
public sealed class HotkeyService : IDisposable
{
    private readonly IHotkeySource _raw;
    private readonly IWmiEventSource _wmi;
    private readonly HotkeySettings _settings;
    private readonly Func<FanMode> _currentMode;
    private readonly Func<long> _clock;
    private readonly SignalDebouncer _debouncer;
    private readonly Action<Action> _post;

    // Written on shutdown and read on both callback threads.
    private volatile bool _disposed;

    /// <summary>Raised once per keypress the app can service, on the thread <c>post</c> chose.</summary>
    public event Action<HotkeyAction>? ActionRequested;

    /// <param name="raw">The raw-input channel.</param>
    /// <param name="wmi">The WMI event channel.</param>
    /// <param name="settings">The owner's live hotkey settings.</param>
    /// <param name="currentMode">What the app believes the fans are running.</param>
    /// <param name="clock">A monotonic millisecond clock; defaults to
    /// <see cref="Environment.TickCount64"/>.</param>
    /// <param name="debouncer">Injectable so a test can shorten the window.</param>
    /// <param name="post">How to get an action onto the thread that may act on it. Defaults to
    /// running it inline, which is what a test wants and what a console caller would get; the app
    /// passes the dispatcher, because a WMI notice arrives on a thread-pool callback and ends at a
    /// view-model property and an overlay.</param>
    /// <exception cref="ArgumentNullException"><paramref name="raw"/>, <paramref name="wmi"/>,
    /// <paramref name="settings"/> or <paramref name="currentMode"/> is null.</exception>
    public HotkeyService(
        IHotkeySource raw,
        IWmiEventSource wmi,
        HotkeySettings settings,
        Func<FanMode> currentMode,
        Func<long>? clock = null,
        SignalDebouncer? debouncer = null,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(currentMode);

        _raw = raw;
        _wmi = wmi;
        _settings = settings;
        _currentMode = currentMode;
        _clock = clock ?? (() => Environment.TickCount64);
        _debouncer = debouncer ?? new SignalDebouncer();
        _post = post ?? (work => work());

        _raw.ReportReceived += OnReport;
        _wmi.EventReceived += OnWmiEvent;
    }

    /// <summary>Opens both channels.</summary>
    /// <remarks>Both are opened even if the first fails: they carry different keys - the fan and
    /// backlight row on one, the touchpad and radio notices on the other - and neither implementation
    /// throws out of <c>Start</c>, so a machine that refuses one still gets the other.</remarks>
    public void Start()
    {
        if (_disposed) return;
        _raw.Start();
        _wmi.Start();
    }

    /// <summary>Whether either channel is open. Only meaningful once <see cref="Start"/> has run.</summary>
    /// <remarks>Either, not both: the two carry different keys and fail for unrelated reasons - a
    /// chassis with no Gigabyte WMI provider is the common case rather than a fault - so half the
    /// Fn row still arriving is the feature working, not a failure to announce.</remarks>
    public bool IsListening => _raw.IsListening || _wmi.IsListening;

    /// <summary>
    /// Why nothing at all is being listened for, in the channels' own words, or null while either
    /// channel is open.
    /// </summary>
    /// <remarks>
    /// Both reasons, because either one alone would name half a failure the owner cannot act on.
    /// Null before <see cref="Start"/> and null when neither channel said why: this is what a
    /// notice is built from, and a notice with nothing in it is worse than none.
    /// </remarks>
    public string? StartError
    {
        get
        {
            if (IsListening) return null;
            var reasons = new[] { _raw.StartError, _wmi.StartError }
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .ToArray();
            return reasons.Length == 0 ? null : string.Join(" ", reasons);
        }
    }

    private void OnReport(byte[] report) => Handle(RawInputDecoder.Decode(report));

    private void OnWmiEvent(int data) => Handle(WmiEventDecoder.Decode(data));

    /// <summary>Debounces one decoded signal, decides about it, and hands the decision on.</summary>
    /// <param name="signal">The decoder's answer, or null if it recognised nothing.</param>
    private void Handle(HotkeyEvent? signal)
    {
        // The null join. See the class remarks: without this, an unrecognised report becomes an
        // ArgumentNullException out of HotkeyPolicy.Decide, on a callback, for input this app was
        // never meant to act on in the first place.
        if (signal is null || _disposed) return;

        // Before the policy rather than after it, so a held key costs one dictionary lookup per
        // report instead of a decision and an allocation per report.
        if (!_debouncer.ShouldFire(signal, _clock())) return;

        var action = HotkeyPolicy.Decide(signal, _currentMode(), _settings);
        if (action.Outcome == HotkeyOutcome.Ignore) return;

        var handler = ActionRequested;
        if (handler is null) return;

        // Read again on the far side: the hand-off is asynchronous in the app, so shutdown can run
        // between the two, and there is nothing left to act on by then.
        _post(() => { if (!_disposed) handler(action); });
    }

    /// <summary>Closes both channels and stops raising anything.</summary>
    /// <remarks>Unsubscribes before disposing, so a message already in the window's queue or a
    /// provider callback already in flight finds nobody listening rather than a half-torn-down
    /// service. Safe to call twice; the app's shutdown path calls it once.</remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _raw.ReportReceived -= OnReport;
        _wmi.EventReceived -= OnWmiEvent;
        _raw.Dispose();
        _wmi.Dispose();
    }
}
