using OpenAorus.App.Hotkeys;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The one part of the raw-input registration that can be checked without a desktop, and the
/// part that matters most.
/// </summary>
/// <remarks>
/// <para>
/// Gigabyte's own shortcut process registers five usages, two of which are the standard keyboard
/// and mouse pages. Under RIDEV_INPUTSINK those deliver every keystroke typed anywhere on the
/// machine into this elevated process for no purpose. Cutting them is what makes "OpenAorus never
/// draws a volume overlay" a fact about the registration rather than a promise about the code.
/// </para>
/// <para>
/// Everything else in <see cref="RawInputWindow"/> - the registration call itself, the
/// message-only window, the window procedure - needs a desktop and a keyboard. It is OWNER
/// VERIFY work: VERIFY 7.1 and 7.2. What is left testable here is the usage table, the native
/// array built from it, the documented constants, the allocation bound, and the promise that a
/// failure to start is quiet.
/// </para>
/// <para>
/// OWNER VERIFY, AND IT CANNOT BE ANYTHING ELSE: the branch where <c>RegisterRawInputDevices</c>
/// returns FALSE - including the <c>Marshal.GetLastWin32Error()</c> read that has to happen on
/// the very next line to be meaningful - has no coverage here and can get none. Reaching it needs
/// a real window handle, which needs a desktop and an STA thread, so no test in this project can
/// make the call happen at all, let alone make it fail. It joins the registration call, the
/// window and the window procedure on the list VERIFY 7.1 and 7.2 exist to work through. What
/// this file can pin is only that the failure is quiet and says where it happened.
/// </para>
/// </remarks>
public class RawInputWindowTests
{
    [Fact]
    public void Exactly_three_vendor_collections_are_registered()
    {
        Assert.Equal(3, RawInputWindow.Usages.Count);
        Assert.Contains(((ushort)0xFF01, (ushort)0x2209), RawInputWindow.Usages);
        Assert.Contains(((ushort)0xFF02, (ushort)0x0001), RawInputWindow.Usages);
        Assert.Contains(((ushort)0xFF00, (ushort)0xFF00), RawInputWindow.Usages);
    }

    [Fact]
    public void The_standard_keyboard_and_mouse_pages_are_never_registered()
    {
        // A regression here is a keylogger's data flow, not a cosmetic problem.
        Assert.DoesNotContain(((ushort)0x0001, (ushort)0x0006), RawInputWindow.Usages);
        Assert.DoesNotContain(((ushort)0x0001, (ushort)0x0002), RawInputWindow.Usages);
    }

    [Fact]
    public void The_consumer_control_page_is_never_registered()
    {
        // Volume lives here, and Windows already draws its overlay for it. Not registering the
        // page is why this app cannot draw a second one even by mistake.
        Assert.DoesNotContain(RawInputWindow.Usages, u => u.Page == 0x000C);
    }

    [Fact]
    public void Every_registered_page_is_a_vendor_defined_one()
    {
        // Vendor-defined pages start at 0xFF00. Anything below is a standard page and does not
        // belong in this list.
        Assert.All(RawInputWindow.Usages, u => Assert.True(u.Page >= 0xFF00, $"page {u.Page:X4}"));
    }

    [Fact]
    public void No_collection_is_registered_twice()
    {
        // A duplicate entry has no documented meaning that could be relied on either way, and the
        // table is meant to say what this app listens to once. Whether it would be ignored, or
        // take the whole array down with it and disable every Fn key at once, is exactly the
        // question nobody should have to answer from a bug report.
        Assert.Equal(RawInputWindow.Usages.Count, RawInputWindow.Usages.Distinct().Count());
    }

    [Fact]
    public void The_usage_table_cannot_be_written_through_its_own_type()
    {
        // IReadOnlyList<T> over a bare array casts straight back to the array. The keyboard page
        // being absent is supposed to be structural, and a static table anything in the process
        // could append to would make it a promise about the code instead.
        Assert.Null(RawInputWindow.Usages as (ushort Page, ushort Usage)[]);
        Assert.True(((System.Collections.IList)RawInputWindow.Usages).IsReadOnly);
    }

    [Fact]
    public void The_message_and_flag_constants_are_the_documented_ones()
    {
        Assert.Equal(0x00FF, RawInputWindow.WmInput);
        Assert.Equal(0x00000100, RawInputWindow.RidevInputSink);
        Assert.Equal(0x00000001, RawInputWindow.RidevRemove);
    }

    [Fact]
    public void The_packet_cap_can_never_be_what_rejects_a_readable_packet()
    {
        // The cap exists to bound an allocation made from a length the OS reports, not to filter
        // input. If it ever fell below what RawInputBuffer would accept it would start dropping
        // real reports - silently, since the walk would never see them.
        var largest = RawInputBuffer.HeaderSize + 8 + (RawInputBuffer.MaxReports * RawInputBuffer.MaxReportBytes);

        Assert.True(RawInputWindow.MaxPacketBytes >= largest,
            $"cap {RawInputWindow.MaxPacketBytes} is below the {largest} bytes the walk would accept");
    }

    [Fact]
    public void A_trace_is_not_optional()
    {
        // Handing the window a trace is what makes a rejected report observable. Making it a
        // constructor argument is what stops someone wiring the window up without one.
        Assert.Throws<ArgumentNullException>(() => new RawInputWindow(null!));
    }

    [Fact]
    public void Disposing_a_window_that_never_started_is_quiet()
    {
        var window = new RawInputWindow(new HotkeyTrace());

        window.Dispose();
        window.Dispose();   // shutdown runs once, but must not depend on that

        Assert.False(window.IsListening);
    }

    [Fact]
    public void Failing_to_start_never_throws_and_never_claims_to_be_listening()
    {
        // This runs on a test thread with no desktop and no STA apartment, so the window cannot
        // be created here - which is exactly the shape of the failure this has to survive on a
        // machine where the registration is refused. What it pins is the contract: Start never
        // throws, and a window that did not start says so instead of pretending.
        var trace = new HotkeyTrace();
        var window = new RawInputWindow(trace);

        window.Start();
        window.Start();     // a second call does nothing, failed or not

        // ASSERTED, NOT BRANCHED ON. The failure path is what runs here because an xunit thread is
        // MTA and WPF refuses to build a window on one, and that is a fact about the runner rather
        // than a promise. Hiding the assertions behind "if (!IsListening)" made this test go green
        // on the day someone adds an STA runner setting while covering nothing at all - the same
        // invisible failure the whole hotkey trace exists to eliminate. So the precondition is
        // pinned instead: if the apartment ever changes, this fails loudly and is rewritten, and
        // it never quietly stops testing anything.
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
        Assert.False(window.IsListening);
        Assert.NotNull(window.StartError);
        Assert.Contains("Fn", window.StartError);
        Assert.Equal(1, trace.FaultCount);              // said once, not once per attempt

        window.Dispose();
        Assert.False(window.IsListening);
    }

    [Fact]
    public void A_start_that_never_reached_the_registration_is_not_filed_under_it()
    {
        // The window cannot be built on this thread, so nothing is ever registered. Naming the
        // registration anyway would put "fault in raw-input registration: InvalidOperationException:
        // The calling thread must be STA" in the dump and send the one hardware session after a
        // call that never happened.
        var trace = new HotkeyTrace();
        var window = new RawInputWindow(trace);

        window.Start();

        Assert.False(window.IsListening);
        var text = trace.Render();
        Assert.Contains("raw-input window creation", text);
        Assert.DoesNotContain("fault in raw-input registration", text);

        window.Dispose();
    }

    [Fact]
    public void The_registration_array_is_built_from_the_usage_table_and_nothing_else()
    {
        // The five tests above read Usages. Usages is not what registers - this array is, and it
        // is built by a method they never touch. An edit that hardcoded the standard keyboard page
        // in there would leave all five green, which is the last gap in the guard.
        var devices = RawInputWindow.Devices(RawInputWindow.RidevInputSink, IntPtr.Zero);

        Assert.Equal(
            RawInputWindow.Usages.Select(u => (u.Page, u.Usage)).ToArray(),
            devices.Select(d => (d.UsagePage, d.Usage)).ToArray());
        Assert.All(devices, d => Assert.Equal(RawInputWindow.RidevInputSink, d.Flags));
    }

    [Fact]
    public void Giving_the_collections_back_asks_for_the_same_ones_with_a_null_target()
    {
        // RIDEV_REMOVE has to name exactly what was registered, and wants a null target; an
        // unregistration that missed a collection would leave this process on the OS's list for
        // it after the window is gone.
        var devices = RawInputWindow.Devices(RawInputWindow.RidevRemove, IntPtr.Zero);

        Assert.Equal(
            RawInputWindow.Usages.Select(u => (u.Page, u.Usage)).ToArray(),
            devices.Select(d => (d.UsagePage, d.Usage)).ToArray());
        Assert.All(devices, d => Assert.Equal(IntPtr.Zero, d.Target));
    }
}

/// <summary>
/// The WMI half. Reaching its subscription needs an elevated process and a Gigabyte provider, so
/// what is testable is only that the thing can be built and torn down without touching either.
/// </summary>
/// <remarks>Its live behaviour is OWNER VERIFY work: VERIFY 7.1 is what says whether the
/// subscription is accepted and whether the value really arrives under a property named
/// <c>Data</c>.</remarks>
public class WmiEventListenerTests
{
    [Fact]
    public void A_trace_is_not_optional()
    {
        Assert.Throws<ArgumentNullException>(() => new WmiEventListener(null!));
    }

    [Fact]
    public void Disposing_a_listener_that_never_started_is_quiet()
    {
        var listener = new WmiEventListener(new HotkeyTrace());

        listener.Dispose();
        listener.Dispose();

        Assert.False(listener.IsListening);
        Assert.Null(listener.StartError);
    }
}
