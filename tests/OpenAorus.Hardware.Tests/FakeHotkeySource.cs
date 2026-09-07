using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>A keyboard that types whatever a test tells it to.</summary>
/// <remarks>
/// <para>
/// Deliberately no more permissive than the window it stands in for. Everything the real source
/// can raise came out of <see cref="RawInputBuffer.Reports"/>, so it is a fresh array, never empty
/// and never longer than <see cref="RawInputBuffer.MaxReportBytes"/>, and it can only arrive
/// between <see cref="Start"/> and <see cref="Dispose"/>. A fake that accepted more than that
/// would let a test pass on input no keyboard can produce, and every test built on it would be
/// quietly weaker than it looked.
/// </para>
/// </remarks>
public sealed class FakeHotkeySource : IHotkeySource
{
    public event Action<byte[]>? ReportReceived;

    /// <summary>Whether anyone opened the channel.</summary>
    public bool Started { get; private set; }

    /// <summary>Whether the channel was closed again.</summary>
    public bool Disposed { get; private set; }

    /// <summary>How many reports have been delivered, for tests that count rather than inspect.</summary>
    public int EmittedCount { get; private set; }

    public void Start()
    {
        // The real window registers its devices once and ignores a second call; so does this,
        // rather than pretending a second registration is an error a caller could hit.
        if (Disposed) throw new ObjectDisposedException(nameof(FakeHotkeySource));
        Started = true;
    }

    /// <summary>Delivers one report, exactly as the vendor collection would.</summary>
    /// <param name="report">The report bytes. Must be a length the walk could have produced.</param>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    /// <exception cref="ArgumentException">The report is empty or longer than
    /// <see cref="RawInputBuffer.MaxReportBytes"/> - neither can come off the real path.</exception>
    /// <exception cref="InvalidOperationException">The channel was never opened, or was closed.
    /// Reports outside that window cannot reach a real subscriber, so a test that expects them is
    /// testing something that will not happen.</exception>
    public void Emit(params byte[] report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Length is 0 or > RawInputBuffer.MaxReportBytes)
            throw new ArgumentException(
                $"A report is 1 to {RawInputBuffer.MaxReportBytes} bytes; the walk rejects anything else.",
                nameof(report));
        if (!Started) throw new InvalidOperationException("Start() was never called on this source.");
        if (Disposed) throw new InvalidOperationException("This source has been disposed.");

        EmittedCount++;
        // Copied for the same reason the walk copies: the subscriber owns the array it is given,
        // and handing over the test's own literal would couple the two.
        ReportReceived?.Invoke((byte[])report.Clone());
    }

    public void Dispose() => Disposed = true;
}

/// <summary>A WMI subscription that fires whatever a test tells it to.</summary>
/// <remarks>
/// No value is filtered: <c>GB_WMIACPI_Event</c> can carry any <c>Data</c> at all, and deciding
/// which values mean something is <see cref="WmiEventDecoder"/>'s job. The lifetime is held to
/// the same rule as the raw-input source, because an unopened subscription delivers nothing.
/// </remarks>
public sealed class FakeWmiEventSource : IWmiEventSource
{
    public event Action<int>? EventReceived;

    /// <summary>Whether anyone opened the subscription.</summary>
    public bool Started { get; private set; }

    /// <summary>Whether the subscription was closed again.</summary>
    public bool Disposed { get; private set; }

    /// <summary>How many events have been delivered.</summary>
    public int EmittedCount { get; private set; }

    public void Start()
    {
        if (Disposed) throw new ObjectDisposedException(nameof(FakeWmiEventSource));
        Started = true;
    }

    /// <summary>Delivers one event's <c>Data</c> value.</summary>
    /// <exception cref="InvalidOperationException">The subscription was never opened, or was
    /// closed.</exception>
    public void Emit(int data)
    {
        if (!Started) throw new InvalidOperationException("Start() was never called on this source.");
        if (Disposed) throw new InvalidOperationException("This source has been disposed.");

        EmittedCount++;
        EventReceived?.Invoke(data);
    }

    public void Dispose() => Disposed = true;
}
