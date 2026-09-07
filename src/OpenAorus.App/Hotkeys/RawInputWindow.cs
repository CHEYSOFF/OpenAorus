using System.Runtime.InteropServices;
using System.Windows.Interop;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.App.Hotkeys;

/// <summary>
/// A message-only window that receives <c>WM_INPUT</c> for the keyboard's vendor collections and
/// hands the reports on undecoded.
/// </summary>
/// <remarks>
/// <para>
/// Three usages, not the five Gigabyte's own shortcut process registers. The two it drops are the
/// standard keyboard and mouse pages, which under <c>RIDEV_INPUTSINK</c> would deliver every
/// keystroke typed anywhere on the machine into this elevated process - for nothing, since every
/// documented signal arrives on a vendor collection. The consumer-control page carrying the
/// volume keys is excluded for the same reason and one more: not receiving them is what makes it
/// impossible for this app to draw a second volume overlay. The cost is named in <c>VERIFY.md</c>:
/// if a needed signal turns out to arrive only on the keyboard page, this cut hides it, and
/// VERIFY 8.2 - press every Fn combination, write down which do nothing - is what would show that.
/// </para>
/// <para>
/// Deliberately thin. It receives, it writes down what arrived, and it hands the bytes to
/// <see cref="IHotkeySource.ReportReceived"/> without looking at them. Everything that decides
/// what a report means lives above the seam where a test can reach it, and the reports it hands
/// on are read by <see cref="RawInputDecoder"/> under the byte-indexing assumption described in
/// that class's remarks - <c>bRawData1</c> is <c>report[0]</c>, a reading of decompiled field
/// names and not an observation. IF A WHOLE FN ROW IS DEAD ON THE BENCH, START THERE: the trace
/// this window writes says whether the report arrived at all, and the decoder's remarks say what
/// would then be misread.
/// </para>
/// <para>
/// One thing the wiring above has to get right, because nothing down here can: the bytes handed
/// on go to <see cref="RawInputDecoder.Decode(byte[])"/>, which returns null for a report it does
/// not recognise, and <see cref="HotkeyPolicy.Decide"/> throws on a null signal. Those two have
/// to be joined by a null check, or an unrecognised report - which arrives regardless of focus -
/// becomes an unhandled exception on a callback.
/// </para>
/// <para>
/// Nothing here is exercised by a test beyond <see cref="Usages"/>, the constants and the
/// quiet-failure contract. The registration call, the window and the window procedure need a
/// desktop; VERIFY 8.1 and 8.2 are what confirm them.
/// </para>
/// </remarks>
public sealed class RawInputWindow : IHotkeySource
{
    /// <summary>The vendor collections this app listens to, and nothing else.</summary>
    public static IReadOnlyList<(ushort Page, ushort Usage)> Usages { get; } = new[]
    {
        ((ushort)0xFF01, (ushort)0x2209),
        ((ushort)0xFF02, (ushort)0x0001),   // the Fn hotkey collection
        ((ushort)0xFF00, (ushort)0xFF00),
    };

    /// <summary><c>WM_INPUT</c>.</summary>
    public const int WmInput = 0x00FF;

    /// <summary><c>RIDEV_INPUTSINK</c>: deliver input regardless of which window has focus.</summary>
    public const int RidevInputSink = 0x00000100;

    /// <summary><c>RIDEV_REMOVE</c>: give the collections back, on shutdown.</summary>
    public const int RidevRemove = 0x00000001;

    /// <summary>The largest <c>WM_INPUT</c> packet this window will allocate for.</summary>
    /// <remarks>Not a filter. The length comes from the OS and is realistically under a hundred
    /// bytes; this only stops an implausible one turning into an implausible allocation on a
    /// callback. It sits far above the largest packet <see cref="RawInputBuffer"/>'s own caps
    /// could ever accept, and a test pins that it stays there - a cap that fell below them would
    /// start dropping real reports where nothing could see it happen.</remarks>
    public const int MaxPacketBytes = 4096;

    /// <summary><c>RID_INPUT</c>: ask <c>GetRawInputData</c> for the packet, not just its header.</summary>
    private const uint RidInput = 0x10000003;

    private const string MessageSite = "WM_INPUT";
    private const string RegistrationSite = "raw-input registration";
    private const string TeardownSite = "raw-input teardown";

    /// <summary><c>HWND_MESSAGE</c>.</summary>
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HotkeyTrace _trace;

    // Reused across messages, and always exactly the length the last call reported - never
    // longer. RawInputBuffer is safe either way, but a stale tail left over from a bigger message
    // can satisfy a dwCount that overstates the packet, and an exact-length buffer removes that.
    private byte[] _receive = Array.Empty<byte>();

    private HwndSource? _source;
    private bool _started;
    private bool _registered;

    /// <summary>Creates the window. Nothing happens until <see cref="Start"/>.</summary>
    /// <param name="trace">Where arriving reports are written down. Not optional: a report that
    /// decodes to nothing has to be observable, or a wrong reading of the report bytes and a
    /// chassis that emits no reports look identical on the bench.</param>
    /// <exception cref="ArgumentNullException"><paramref name="trace"/> is null.</exception>
    public RawInputWindow(HotkeyTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        _trace = trace;
    }

    /// <inheritdoc />
    public event Action<byte[]>? ReportReceived;

    /// <summary>Whether the collections were actually registered.</summary>
    public bool IsListening { get; private set; }

    /// <summary>What went wrong, if the channel could not be opened; null otherwise.</summary>
    /// <remarks>Set once, by <see cref="Start"/>, so the owner is told once rather than per
    /// message. Nothing on the message path ever writes it.</remarks>
    public string? StartError { get; private set; }

    /// <inheritdoc />
    public void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            // Message-only: no desktop presence, no taskbar entry, and it cannot be activated.
            var parameters = new HwndSourceParameters("OpenAorus.RawInput")
            {
                ParentWindow = HwndMessage,
                Width = 0,
                Height = 0,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(OnMessage);

            var devices = Devices(RidevInputSink, _source.Handle);
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
            {
                // Read immediately: anything else on this line could clear it.
                var error = Marshal.GetLastWin32Error();
                Fail($"Win32 error {error}");
                return;
            }

            _registered = true;
            IsListening = true;
        }
        catch (Exception ex)
        {
            // Broad on purpose. A machine where the window cannot be created is a machine with no
            // Fn keys, not a machine that fails to start: the WMI channel may still work, the fan
            // and lighting panels are untouched, and the app has to come up either way.
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Gives the collections back and destroys the window.</summary>
    public void Dispose() => SafeTearDown();

    private void Fail(string detail)
    {
        IsListening = false;
        StartError = "The Fn keys cannot be listened for: " + detail;
        _trace.RecordFault(RegistrationSite, detail);
        SafeTearDown();
    }

    /// <summary>Tears down without raising anything.</summary>
    /// <remarks>Both callers are paths with nobody left to report to - shutdown, and a start that
    /// has already failed - and a throw from either would undo the promise the failure is being
    /// handled for.</remarks>
    private void SafeTearDown()
    {
        try
        {
            TearDown();
        }
        catch (Exception ex)
        {
            _trace.RecordFault(TeardownSite, ex);
        }
    }

    private void TearDown()
    {
        if (_registered)
        {
            _registered = false;
            // RIDEV_REMOVE wants a null target; the return value is not worth acting on, because
            // the only remedy for a failed unregistration is the process exit that follows.
            var devices = Devices(RidevRemove, IntPtr.Zero);
            RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
        }

        IsListening = false;
        if (_source is null) return;

        _source.RemoveHook(OnMessage);
        _source.Dispose();
        _source = null;
    }

    private static RawInputDevice[] Devices(int flags, IntPtr target) => Usages
        .Select(u => new RawInputDevice
        {
            UsagePage = u.Page,
            Usage = u.Usage,
            Flags = flags,
            Target = target,
        })
        .ToArray();

    /// <summary>The window procedure hook.</summary>
    /// <remarks>An exception thrown out of here is raised inside the message loop, where there is
    /// no caller to catch it and nothing on screen to say what happened - so everything is caught,
    /// including the programming errors the decoders deliberately throw on rather than laundering
    /// into silence. The catch cannot spin: <see cref="HotkeyTrace.RecordFault(string, string)"/>
    /// lists one line per site however often it repeats, and nothing here raises a banner.</remarks>
    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmInput) return IntPtr.Zero;

        // Not marked handled: this is an input sink, and swallowing the message would take the
        // key away from whatever program the owner is actually typing into.
        try
        {
            Receive(lParam);
        }
        catch (Exception ex)
        {
            _trace.RecordFault(MessageSite, ex);
        }

        return IntPtr.Zero;
    }

    private void Receive(IntPtr packet)
    {
        // The header size is the one RawInputBuffer's walk assumes. It is also checked for us:
        // GetRawInputData is documented to fail outright if it does not match the real
        // RAWINPUTHEADER, so a wrong value here is a dead channel rather than a misread packet.
        var headerBytes = (uint)RawInputBuffer.HeaderSize;

        var size = 0u;
        if (GetRawInputData(packet, RidInput, IntPtr.Zero, ref size, headerBytes) != 0 || size == 0)
        {
            _trace.RecordUnreadablePacket(0);
            return;
        }

        if (size > MaxPacketBytes)
        {
            _trace.RecordUnreadablePacket(size);
            return;
        }

        if (_receive.Length != size) _receive = new byte[size];

        uint copied;
        var pin = GCHandle.Alloc(_receive, GCHandleType.Pinned);
        try
        {
            copied = GetRawInputData(packet, RidInput, pin.AddrOfPinnedObject(), ref size, headerBytes);
        }
        finally
        {
            pin.Free();
        }

        if (copied != (uint)_receive.Length)
        {
            _trace.RecordUnreadablePacket(_receive.Length);
            return;
        }

        var reports = RawInputBuffer.Reports(_receive);
        if (reports.Count == 0)
        {
            // Written down rather than dropped. A packet that arrives and holds nothing readable
            // is what an x64 header assumption gone wrong looks like, and it is invisible
            // otherwise - see RawInputBuffer's remarks and HotkeyTrace's.
            _trace.RecordUnreadablePacket(_receive.Length);
            return;
        }

        foreach (var report in reports)
        {
            _trace.RecordReport(report);
            ReportReceived?.Invoke(report);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public int Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
}
