using OpenAorus.App.Hotkeys;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The whole path from a four-byte report to a requested action, over fakes.
/// </summary>
/// <remarks>
/// This is what the undecoded seams bought: one fake source and one fake event stream cover the
/// decoder, the debouncer and the policy together, so the only untested thing left between a
/// keypress and a fan write is the registration call itself.
/// </remarks>
public class HotkeyServiceTests
{
    private static HotkeySettings AllOn() => new()
    {
        Enabled = true,
        OverlayForFanMode = true,
        OverlayForBacklight = true,
        OverlayForTouchpad = true,
        OverlayForWifi = true,
    };

    private sealed class Rig : IDisposable
    {
        public FakeHotkeySource Raw { get; } = new();
        public FakeWmiEventSource Wmi { get; } = new();
        public HotkeySettings Settings { get; } = AllOn();
        public List<HotkeyAction> Actions { get; } = new();
        public FanMode Mode { get; set; } = FanMode.Quiet;
        public long Now { get; set; }
        public HotkeyService Service { get; }

        public Rig()
        {
            Service = new HotkeyService(Raw, Wmi, Settings, () => Mode, () => Now);
            Service.ActionRequested += Actions.Add;
            Service.Start();
        }

        public void Dispose() => Service.Dispose();
    }

    // ---- Opening and closing the channels --------------------------------------------

    [Fact]
    public void Starting_opens_both_channels()
    {
        using var rig = new Rig();

        Assert.True(rig.Raw.Started);
        Assert.True(rig.Wmi.Started);
    }

    [Fact]
    public void Disposing_closes_both_channels()
    {
        var rig = new Rig();
        rig.Dispose();

        Assert.True(rig.Raw.Disposed);
        Assert.True(rig.Wmi.Disposed);
    }

    [Fact]
    public void Disposing_twice_is_quiet()
    {
        var rig = new Rig();

        rig.Dispose();
        rig.Dispose();

        Assert.True(rig.Raw.Disposed);
    }

    // ---- What each channel produces --------------------------------------------------

    [Fact]
    public void A_fan_report_becomes_a_cycle_to_the_next_mode()
    {
        using var rig = new Rig();

        rig.Raw.Emit(4, 0, 0, 39);

        var action = Assert.Single(rig.Actions);
        Assert.Equal(HotkeyOutcome.CycleFanMode, action.Outcome);
        Assert.Equal(FanMode.Normal, action.Mode);
        Assert.True(action.ShowOverlay);
    }

    [Fact]
    public void The_cycle_is_read_from_the_mode_the_app_is_actually_in()
    {
        using var rig = new Rig { Mode = FanMode.Gaming };

        rig.Raw.Emit(4, 0, 0, 37);

        Assert.Equal(FanMode.Turbo, Assert.Single(rig.Actions).Mode);
    }

    [Fact]
    public void A_backlight_report_carries_the_level_the_firmware_moved_to()
    {
        using var rig = new Rig();

        rig.Raw.Emit(4, 1, 50, 0);

        var action = Assert.Single(rig.Actions);
        Assert.Equal(HotkeyOutcome.SetBacklightLevel, action.Outcome);
        Assert.Equal(100, action.Level);
    }

    [Fact]
    public void A_wmi_event_becomes_a_notice()
    {
        using var rig = new Rig();

        rig.Wmi.Emit(202);

        var action = Assert.Single(rig.Actions);
        Assert.Equal(HotkeyOutcome.Notify, action.Outcome);
        Assert.Contains("Touchpad", action.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_channels_feed_the_one_stream_of_actions()
    {
        using var rig = new Rig();

        rig.Raw.Emit(4, 0, 0, 39);
        rig.Wmi.Emit(450);

        Assert.Collection(rig.Actions,
            a => Assert.Equal(HotkeyOutcome.CycleFanMode, a.Outcome),
            a => Assert.Equal(HotkeyOutcome.Notify, a.Outcome));
    }

    // ---- Debounce --------------------------------------------------------------------

    [Fact]
    public void The_same_report_twice_inside_the_window_acts_once()
    {
        using var rig = new Rig();

        rig.Raw.Emit(4, 0, 0, 39);
        rig.Now += SignalDebouncer.WindowMs - 1;
        rig.Raw.Emit(4, 0, 0, 39);

        Assert.Single(rig.Actions);
    }

    [Fact]
    public void The_same_report_outside_the_window_acts_twice()
    {
        using var rig = new Rig();

        rig.Raw.Emit(4, 0, 0, 39);
        rig.Now += SignalDebouncer.WindowMs;
        rig.Raw.Emit(4, 0, 0, 39);

        Assert.Equal(2, rig.Actions.Count);
    }

    // ---- Nothing this app services ---------------------------------------------------

    [Fact]
    public void A_brightness_report_produces_no_action_at_all()
    {
        using var rig = new Rig();

        rig.Raw.Emit(9, 0, 1, 3, 0, 60, 0, 0, 0);

        // The bug this release fixes: Windows draws that overlay already.
        Assert.Empty(rig.Actions);
    }

    [Theory]
    [InlineData(new byte[] { 4, 0, 0, 200 })]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 4, 1, 33, 0 })]
    [InlineData(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9 })]
    [InlineData(new byte[] { 0 })]
    public void A_report_it_cannot_decode_produces_nothing(byte[] report)
    {
        using var rig = new Rig();

        // RawInputDecoder.Decode returns null here and HotkeyPolicy.Decide throws on null, so a
        // service that joined the two without a null check would raise an ArgumentNullException
        // out of Emit - which on the real path is an unhandled exception on a callback that fires
        // regardless of focus. Emit not throwing is half of what this pins.
        rig.Raw.Emit(report);

        Assert.Empty(rig.Actions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void An_event_value_it_cannot_decode_produces_nothing(int data)
    {
        using var rig = new Rig();

        // The same null join on the other channel, where the callback is a thread-pool one and an
        // exception would stop the subscription without anyone seeing it.
        rig.Wmi.Emit(data);

        Assert.Empty(rig.Actions);
    }

    [Fact]
    public void An_undecodable_report_does_not_stop_the_next_real_one()
    {
        using var rig = new Rig();

        rig.Raw.Emit(4, 0, 0, 200);
        rig.Raw.Emit(4, 0, 0, 39);

        Assert.Single(rig.Actions);
    }

    [Fact]
    public void Switching_hotkeys_off_at_run_time_stops_the_actions_without_restarting()
    {
        using var rig = new Rig();
        rig.Settings.Enabled = false;

        rig.Raw.Emit(4, 0, 0, 39);
        rig.Wmi.Emit(450);

        // The settings object is the live one the Settings window edits, so a toggle takes
        // effect on the next keypress rather than on the next launch.
        Assert.Empty(rig.Actions);
    }

    // ---- The hand-off to the UI thread -----------------------------------------------

    [Fact]
    public void Every_action_goes_through_the_hand_off_the_owner_supplied()
    {
        // Raw input arrives on the message window's thread and WMI on a thread-pool callback, and
        // both end at view-model properties and an overlay. Nothing may be raised inline on the
        // arriving thread when a hand-off was supplied.
        var posted = new List<Action>();
        var actions = new List<HotkeyAction>();
        var raw = new FakeHotkeySource();
        var wmi = new FakeWmiEventSource();
        using var service = new HotkeyService(
            raw, wmi, AllOn(), () => FanMode.Quiet, () => 0, post: posted.Add);
        service.ActionRequested += actions.Add;
        service.Start();

        raw.Emit(4, 0, 0, 39);
        wmi.Emit(202);

        Assert.Equal(2, posted.Count);
        Assert.Empty(actions);

        foreach (var work in posted) work();
        Assert.Equal(2, actions.Count);
    }

    [Fact]
    public void An_action_that_arrives_at_the_ui_thread_after_shutdown_is_dropped()
    {
        // The dispatcher runs the queued work later; by then the owner may have quit, and the
        // view model this would call is gone.
        var posted = new List<Action>();
        var actions = new List<HotkeyAction>();
        var raw = new FakeHotkeySource();
        var service = new HotkeyService(
            raw, new FakeWmiEventSource(), AllOn(), () => FanMode.Quiet, () => 0, post: posted.Add);
        service.ActionRequested += actions.Add;
        service.Start();
        raw.Emit(4, 0, 0, 39);

        service.Dispose();
        foreach (var work in posted) work();

        Assert.Empty(actions);
    }

    // ---- The trace, end to end -------------------------------------------------------
    //
    // These drive the real sources rather than the fakes. Neither can be started here - the
    // window needs a desktop and the subscription an elevated Gigabyte provider - but Deliver is
    // exactly what the window procedure and the event callback call once they have bytes, so the
    // order of "write it down" and "hand it on" is reachable, and that order is the whole point
    // of the trace: it is what tells "nothing arrived" from "reports arrived and nothing
    // happened" from "packets arrived unreadable".

    private static HotkeyService Wire(IHotkeySource raw, IWmiEventSource wmi, List<HotkeyAction> actions)
    {
        var service = new HotkeyService(raw, wmi, AllOn(), () => FanMode.Quiet, () => 0);
        service.ActionRequested += actions.Add;
        return service;
    }

    [Fact]
    public void Every_report_reaches_the_trace_including_the_ones_that_decode_to_nothing()
    {
        var trace = new HotkeyTrace();
        var window = new RawInputWindow(trace);
        var actions = new List<HotkeyAction>();
        using var service = Wire(window, new FakeWmiEventSource(), actions);

        window.Deliver(new[] { new byte[] { 4, 0, 0, 39 }, new byte[] { 4, 0, 0, 200 } });

        Assert.Equal(2, trace.ReportCount);
        Assert.Single(actions);

        // Written down by their bytes, so VERIFY 8.2 can read what the keyboard actually sent
        // rather than only that something did.
        var dump = trace.Render();
        Assert.Contains("04 00 00 27", dump, StringComparison.Ordinal);
        Assert.Contains("04 00 00 C8", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_event_reaches_the_trace_including_the_ones_that_decode_to_nothing()
    {
        var trace = new HotkeyTrace();
        var listener = new WmiEventListener(trace);
        var actions = new List<HotkeyAction>();
        using var service = Wire(new RawInputWindow(trace), listener, actions);

        listener.Deliver(202, Array.Empty<string>());
        listener.Deliver(1, Array.Empty<string>());

        Assert.Equal(2, trace.EventCount);
        Assert.Single(actions);
        Assert.Contains("Data=1", trace.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_whose_value_could_not_be_read_is_written_down_with_what_did_arrive()
    {
        var trace = new HotkeyTrace();
        var listener = new WmiEventListener(trace);
        var actions = new List<HotkeyAction>();
        using var service = Wire(new RawInputWindow(trace), listener, actions);

        // A wrong property name is a subscription that runs forever and reports nothing; the
        // names that did arrive are the only thing that would say so.
        listener.Deliver("202", new[] { "Brightness", "TIME_CREATED" });

        Assert.Equal(0, trace.EventCount);
        Assert.Equal(1, trace.UnreadableEventCount);
        Assert.Empty(actions);
        Assert.Contains("Brightness", trace.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_that_lands_after_shutdown_does_nothing()
    {
        // A WM_INPUT already in the queue, or a WMI callback already in flight, can arrive after
        // shutdown has run. FakeHotkeySource refuses to emit once disposed - deliberately - so
        // this uses the real window, whose Deliver is what the window procedure calls.
        var trace = new HotkeyTrace();
        var window = new RawInputWindow(trace);
        var actions = new List<HotkeyAction>();
        var service = Wire(window, new FakeWmiEventSource(), actions);
        service.Dispose();

        window.Deliver(new[] { new byte[] { 4, 0, 0, 39 } });

        Assert.Empty(actions);
    }
}
