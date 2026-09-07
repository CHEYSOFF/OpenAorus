namespace OpenAorus.Hardware.Hotkeys;

/// <summary>
/// The only door to raw input. Real implementation: <c>OpenAorus.App.Hotkeys.RawInputWindow</c>.
/// </summary>
/// <remarks>
/// <para>
/// It carries the HID report undecoded, exactly as <see cref="Lighting.IKeyboardHid"/> carries
/// undecoded 264-byte reports. Decoding on this side of the seam would put
/// <see cref="RawInputDecoder"/> where no test can reach it; leaving the bytes alone means one
/// fake covers the whole path from a report to an applied fan mode.
/// </para>
/// <para>
/// The seam is this narrow on purpose. Everything above it - the walk, the decoders, the
/// debounce, the policy - is a pure function over bytes; everything below it is
/// <c>RegisterRawInputDevices</c>, a message-only window and a window procedure, none of which
/// can run in a test. Moving any decision downward moves it out of reach.
/// </para>
/// </remarks>
public interface IHotkeySource : IDisposable
{
    /// <summary>Raised for each HID input report that arrives, on the thread that received it.</summary>
    /// <remarks>The array belongs to the subscriber: it is a fresh copy per report, so holding or
    /// mutating it is safe even though the implementation reuses one receive buffer.</remarks>
    event Action<byte[]>? ReportReceived;

    /// <summary>Opens the channel. Safe to call once; a second call does nothing.</summary>
    void Start();

    /// <summary>Whether the channel is open. False before <see cref="Start"/>, and after a
    /// <see cref="Start"/> that could not open it.</summary>
    /// <remarks>On this seam rather than on the implementation alone because a channel that
    /// failed to open is something the owner has to be told about, and the wiring that would tell
    /// them only ever sees the interface.</remarks>
    bool IsListening { get; }

    /// <summary>Why the channel could not be opened, or null if nothing has gone wrong.</summary>
    /// <remarks>Set once, by <see cref="Start"/>, so a channel that misbehaves per message cannot
    /// turn into a notice per message.</remarks>
    string? StartError { get; }
}

/// <summary>
/// The only door to the WMI event stream. Real implementation:
/// <c>OpenAorus.App.Hotkeys.WmiEventListener</c>.
/// </summary>
/// <remarks>Carries the raw <c>Data</c> value for the same reason, and needs elevation where the
/// raw-input channel does not.</remarks>
public interface IWmiEventSource : IDisposable
{
    /// <summary>Raised with one event's <c>Data</c> value.</summary>
    /// <remarks>Undecoded and unfiltered: <c>GB_WMIACPI_Event</c> can carry anything, and
    /// <see cref="WmiEventDecoder"/> is what knows which values mean something.</remarks>
    event Action<int>? EventReceived;

    /// <summary>Opens the subscription. Safe to call once; a second call does nothing.</summary>
    void Start();

    /// <summary>Whether the subscription is running.</summary>
    bool IsListening { get; }

    /// <summary>Why the subscription could not be created, or null if nothing has gone wrong.
    /// Set once, by <see cref="Start"/>.</summary>
    string? StartError { get; }
}
