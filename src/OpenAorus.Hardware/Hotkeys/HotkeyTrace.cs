using System.Text;

namespace OpenAorus.Hardware.Hotkeys;

/// <summary>
/// A bounded record of what the two hotkey channels actually delivered, for the diagnostics dump.
/// </summary>
/// <remarks>
/// <para>
/// THIS EXISTS FOR ONE FAILURE MODE. <see cref="RawInputDecoder"/> rests on a reading of
/// decompiled field names - <c>bRawData1</c> is <c>report[0]</c> - that nobody has checked against
/// hardware; see its remarks. If that reading is inverted, every real report fails the decoder's
/// leading-byte check and is rejected outright, so the bench sees nothing happen at all. "Nothing
/// happens" is also what a chassis that emits no such reports looks like, and the research lists
/// that as an open question. Without a record of what arrived, <c>VERIFY.md</c> 8.2 cannot tell
/// the two apart, and the release cannot be verified.
/// </para>
/// <para>
/// So a report that decodes to nothing is still written down, by its bytes. So is a
/// <c>WM_INPUT</c> packet the walk could make no sense of, which is the separate silent failure
/// <see cref="RawInputBuffer"/> warns about, and an event whose <c>Data</c> property was not
/// where it was expected, which is the one <see cref="WmiEventDecoder.DataProperty"/> warns about.
/// </para>
/// <para>
/// Everything here is bounded before it is written. Both recorders run on callbacks that fire
/// regardless of focus, so anything that can repeat per message costs one line and then only a
/// count: a fault at one site, a packet of one length given up on at one site, an event carrying
/// one set of properties.
/// Reports are the exception and are written every time, because their bytes are the payload -
/// and because a stream of them evicting older ones is the ring working, not a flood. The lines
/// are a ring of <see cref="Capacity"/>; the counts are not capped, because they are the evidence
/// that the channel is alive at all.
/// </para>
/// <para>
/// Locked, not because contention is expected - a keypress is not a hot path - but because
/// <c>WM_INPUT</c> arrives on the window's thread and <c>EventArrived</c> on a WMI callback
/// thread, and the dump is rendered from a third.
/// </para>
/// </remarks>
public sealed class HotkeyTrace
{
    /// <summary>How many entries the ring keeps.</summary>
    /// <remarks>Enough to hold a pass over the whole Fn row, which is what VERIFY 8.2 asks the
    /// owner to do, and small enough that a stuck channel cannot fill a bug report.</remarks>
    public const int Capacity = 32;

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new(Capacity);
    // What has already earned a line. Everything that can repeat per message - a fault at one
    // site, a packet of one shape, an event of one shape - is written once and counted after
    // that, so a channel that misbehaves on every message costs one line rather than filling the
    // ring with copies of itself and evicting the reports that are the point of it.
    private readonly HashSet<string> _saidOnce = new(StringComparer.Ordinal);
    private int _written;

    /// <summary>HID reports handed on to the decoder.</summary>
    public int ReportCount { get; private set; }

    /// <summary><c>WM_INPUT</c> messages that yielded no readable report.</summary>
    public int UnreadablePacketCount { get; private set; }

    /// <summary>WMI events whose <c>Data</c> value was read.</summary>
    public int EventCount { get; private set; }

    /// <summary>WMI events that carried no value this app could read.</summary>
    public int UnreadableEventCount { get; private set; }

    /// <summary>Exceptions caught at a callback boundary, plus channels that failed to start.</summary>
    public int FaultCount { get; private set; }

    /// <summary>Records one HID report, whatever it turns out to mean.</summary>
    /// <param name="report">The report bytes as they came off the wire.</param>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    public void RecordReport(byte[] report)
    {
        // The same line RawInputBuffer and RawInputDecoder draw: no device sends a null array, so
        // it is this app calling itself wrongly. The WM_INPUT boundary catches it either way.
        ArgumentNullException.ThrowIfNull(report);

        lock (_gate)
        {
            ReportCount++;
            Write($"report {report.Length} bytes: {Hex(report)}");
        }
    }

    /// <summary>Records a <c>WM_INPUT</c> message that held nothing this app could read.</summary>
    /// <remarks>
    /// <para>
    /// THE CAUSE IS THE POINT, NOT THE LENGTH. A packet can be unreadable for reasons with
    /// nothing to do with each other - the copy out of the OS came up short, which is a P/Invoke
    /// or WOW64-shaped problem, against the walk finding nothing in a packet it did receive
    /// whole, which is the x64 header-offset reading <see cref="RawInputBuffer"/> warns about and
    /// the reason this trace exists at all. Those have unrelated fixes, so they must not render
    /// as the same line. The length alone does not separate them; the site does.
    /// </para>
    /// <para>
    /// One reading to carry to the dump, because it is otherwise nobody's: a packet of exactly
    /// 36 bytes is <c>24 + 8 + 4</c> - an x64 header, <c>dwSizeHid</c> and <c>dwCount</c>, and one
    /// four-byte report, which is the length a well-formed packet from the documented collections
    /// has. Seeing 36 is therefore evidence FOR the header offset rather than against it, and
    /// points at the copy path instead. That is an inference from the layout, not something
    /// watched on hardware, and VERIFY 8.2 is still what settles it.
    /// </para>
    /// </remarks>
    /// <param name="bytes">The length the OS reported, or 0 if even that could not be read. Taken
    /// as a <see cref="long"/> so an implausible <c>uint</c> reaches the dump as itself rather
    /// than as a negative number.</param>
    /// <param name="cause">Where in the receive path it was given up on, or null for a caller
    /// that has nothing to say. Packets are deduplicated by length and cause together, so this
    /// has to be a constant naming a place in the code - the same rule
    /// <see cref="RecordFault(string, string)"/> draws, and for the same reason.</param>
    public void RecordUnreadablePacket(long bytes, string? cause = null)
    {
        lock (_gate)
        {
            UnreadablePacketCount++;
            var size = bytes <= 0 ? "size unknown" : bytes + " bytes";
            var at = cause is null ? string.Empty : " (" + cause + ")";
            WriteOnce($"packet:{bytes}:{cause}", $"WM_INPUT packet unreadable{at}: {size}");
        }
    }

    /// <summary>Records one WMI event's <c>Data</c> value.</summary>
    /// <param name="data">The value, before anything decides what it means.</param>
    public void RecordEvent(int data)
    {
        lock (_gate)
        {
            EventCount++;
            Write($"WMI event: Data={data}");
        }
    }

    /// <summary>Records a WMI event that carried no readable <c>Data</c>.</summary>
    /// <param name="properties">The property names the event did carry. A wrong assumption about
    /// the name is otherwise a subscription that runs forever and reports nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="properties"/> is null.</exception>
    public void RecordUnreadableEvent(IReadOnlyList<string> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        lock (_gate)
        {
            UnreadableEventCount++;
            var names = properties.Count == 0 ? "no properties" : string.Join(", ", properties);
            WriteOnce($"event:{names}", $"WMI event without a readable {WmiEventDecoder.DataProperty}: {names}");
        }
    }

    /// <summary>Records a failure at one named site.</summary>
    /// <param name="where">The site, e.g. <c>WM_INPUT</c>. Faults are deduplicated by this, so it
    /// has to be a constant naming a place in the code - a string built per message would grow
    /// the set of sites without bound and defeat the deduplication it is the key to.</param>
    /// <param name="detail">What went wrong.</param>
    /// <exception cref="ArgumentNullException"><paramref name="where"/> or
    /// <paramref name="detail"/> is null.</exception>
    public void RecordFault(string where, string detail)
    {
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(detail);

        lock (_gate)
        {
            FaultCount++;
            // One line per site, however often it repeats. A subscriber that throws on every
            // report throws on a callback that fires regardless of focus; the count is what says
            // it is still happening, and a second line would say nothing the first did not.
            WriteOnce($"fault:{where}", $"fault in {where}: {Flatten(detail)}");
        }
    }

    /// <summary>Records an exception caught at one named site.</summary>
    /// <param name="where">The site. Faults are deduplicated by this.</param>
    /// <param name="error">The exception.</param>
    /// <exception cref="ArgumentNullException"><paramref name="where"/> or
    /// <paramref name="error"/> is null.</exception>
    public void RecordFault(string where, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        RecordFault(where, $"{error.GetType().Name}: {error.Message}");
    }

    /// <summary>Renders the section the diagnostics dump carries.</summary>
    /// <returns>The counts, then the entries the ring still holds, one per line.</returns>
    public string Render()
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            sb.Append("Hotkey channels: ")
              .Append("reports=").Append(ReportCount)
              .Append(" unreadable-packets=").Append(UnreadablePacketCount)
              .Append(" events=").Append(EventCount)
              .Append(" unreadable-events=").Append(UnreadableEventCount)
              .Append(" faults=").Append(FaultCount)
              .AppendLine();

            if (_lines.Count == 0)
            {
                // The most informative line in the whole dump when a Fn key does nothing.
                sb.AppendLine("  nothing has arrived");
                return sb.ToString();
            }

            var dropped = _written - _lines.Count;
            if (dropped > 0) sb.Append("  (").Append(dropped).AppendLine(" earlier entries dropped)");
            foreach (var line in _lines) sb.Append("  ").AppendLine(line);
            return sb.ToString();
        }
    }

    /// <summary>Appends a line the first time this shape is seen, and nothing after that.</summary>
    /// <remarks>Callers hold the lock. The set of shapes is bounded by the callers: a site name,
    /// a packet length paired with one of a fixed handful of sites, a property list.</remarks>
    private void WriteOnce(string shape, string line)
    {
        if (_saidOnce.Add(shape)) Write(line);
    }

    /// <summary>Appends one numbered line, dropping the oldest if the ring is full.</summary>
    /// <remarks>Callers hold the lock.</remarks>
    private void Write(string line)
    {
        _written++;
        if (_lines.Count == Capacity) _lines.Dequeue();
        _lines.Enqueue($"#{_written} {line}");
    }

    /// <summary>Puts a detail on one line, whatever it contained.</summary>
    /// <remarks>An exception message with newlines in it would otherwise forge entries in a file
    /// that is read line by line.</remarks>
    private static string Flatten(string text) =>
        text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>One report as spaced hex pairs, which is how the research tables read.</summary>
    private static string Hex(byte[] report)
    {
        var sb = new StringBuilder(report.Length * 3);
        foreach (var b in report)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
