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
/// VERIFY work: VERIFY 8.1 and 8.2. What is left testable here is the usage table, the
/// documented constants, the allocation bound, and the promise that a failure to start is quiet.
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
        // RegisterRawInputDevices refuses the whole array if one entry is a duplicate, so a
        // copy-paste in the table would silently disable every Fn key rather than one.
        Assert.Equal(RawInputWindow.Usages.Count, RawInputWindow.Usages.Distinct().Count());
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

        // In this runner it is the failure path that runs - an xunit test thread is MTA, and WPF
        // refuses to build a window on one - so the catch, the recorded fault and the message are
        // all really executed here. The branch is kept because a runner that happened to be STA
        // would register for real, and a test that then failed would be reporting on the runner.
        if (!window.IsListening)
        {
            Assert.NotNull(window.StartError);
            Assert.Contains("Fn", window.StartError);
            Assert.Equal(1, trace.FaultCount);          // said once, not once per attempt
        }

        window.Dispose();
        Assert.False(window.IsListening);
    }
}

/// <summary>
/// The WMI half. Reaching its subscription needs an elevated process and a Gigabyte provider, so
/// what is testable is only that the thing can be built and torn down without touching either.
/// </summary>
/// <remarks>Its live behaviour is OWNER VERIFY work: VERIFY 8.1 is what says whether the
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
