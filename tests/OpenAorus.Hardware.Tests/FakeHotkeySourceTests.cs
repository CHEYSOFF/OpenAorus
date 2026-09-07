using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The guards on the two fake sources, tested because a fake is only worth what it refuses.
/// </summary>
/// <remarks>
/// Every test above the seam runs on these two classes and on nothing else, so a fake that
/// accepted a report the real path cannot produce would weaken all of them at once without any
/// test going red. These are the tests that go red instead.
/// </remarks>
public class FakeHotkeySourceTests
{
    [Fact]
    public void A_report_reaches_whoever_subscribed()
    {
        var source = new FakeHotkeySource();
        var seen = new List<byte[]>();
        source.ReportReceived += seen.Add;
        source.Start();

        source.Emit(4, 0, 0, 39);

        Assert.True(source.Started);
        Assert.Equal(new byte[] { 4, 0, 0, 39 }, Assert.Single(seen));
        Assert.Equal(1, source.EmittedCount);
    }

    [Fact]
    public void The_subscriber_gets_an_array_of_its_own()
    {
        // The real source hands out a copy the walk just made. A fake that passed the test's own
        // literal would let a consumer mutate it and hide the coupling.
        var source = new FakeHotkeySource();
        byte[]? received = null;
        source.ReportReceived += r => received = r;
        source.Start();

        var report = new byte[] { 4, 0, 0, 37 };
        source.Emit(report);

        Assert.NotSame(report, received);
        Assert.Equal(report, received);
    }

    [Fact]
    public void Emitting_before_the_channel_is_open_is_a_test_bug()
    {
        var source = new FakeHotkeySource();

        Assert.Throws<InvalidOperationException>(() => source.Emit(4, 0, 0, 39));
        Assert.Throws<InvalidOperationException>(() => new FakeWmiEventSource().Emit(202));
    }

    [Fact]
    public void Emitting_after_disposal_is_a_test_bug()
    {
        var source = new FakeHotkeySource();
        source.Start();
        source.Dispose();

        Assert.True(source.Disposed);
        Assert.Throws<InvalidOperationException>(() => source.Emit(4, 0, 0, 39));
    }

    [Fact]
    public void A_report_the_walk_could_never_have_produced_is_refused()
    {
        var source = new FakeHotkeySource();
        source.Start();

        Assert.Throws<ArgumentNullException>(() => source.Emit(null!));
        Assert.Throws<ArgumentException>(() => source.Emit(Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(
            () => source.Emit(new byte[RawInputBuffer.MaxReportBytes + 1]));
        Assert.Equal(0, source.EmittedCount);
    }

    [Fact]
    public void A_report_at_the_longest_length_the_walk_allows_is_accepted()
    {
        var source = new FakeHotkeySource();
        source.Start();
        var seen = 0;
        source.ReportReceived += _ => seen++;

        source.Emit(new byte[RawInputBuffer.MaxReportBytes]);

        Assert.Equal(1, seen);
    }

    [Fact]
    public void Opening_the_channel_twice_is_harmless()
    {
        var source = new FakeHotkeySource();
        source.Start();
        source.Start();

        Assert.True(source.Started);
    }

    [Fact]
    public void Reopening_a_closed_channel_is_not()
    {
        var source = new FakeHotkeySource();
        source.Dispose();

        Assert.Throws<ObjectDisposedException>(source.Start);
    }

    [Fact]
    public void The_wmi_source_passes_any_data_value_through_undecoded()
    {
        // The seam carries the raw Data; WmiEventDecoder decides what it means, and values it
        // has never heard of still have to arrive.
        var source = new FakeWmiEventSource();
        var seen = new List<int>();
        source.EventReceived += seen.Add;
        source.Start();

        source.Emit(202);
        source.Emit(0);
        source.Emit(-1);
        source.Emit(int.MaxValue);

        Assert.Equal(new[] { 202, 0, -1, int.MaxValue }, seen);
        Assert.Equal(4, source.EmittedCount);
    }

    [Fact]
    public void A_source_with_nobody_listening_is_not_an_error()
    {
        // The service subscribes after construction in some orders; an unsubscribed emit is a
        // no-op on the real path too, because a window with no handler simply drops the message.
        var source = new FakeHotkeySource();
        source.Start();

        source.Emit(4, 0, 0, 39);

        Assert.Equal(1, source.EmittedCount);
    }

    [Fact]
    public void Both_sources_are_the_disposable_seams_the_service_will_hold()
    {
        // Pins the shapes the service is written against, so a change to either interface has to
        // be made deliberately rather than discovered in App.
        using IHotkeySource hotkeys = new FakeHotkeySource();
        using IWmiEventSource wmi = new FakeWmiEventSource();

        hotkeys.Start();
        wmi.Start();

        Assert.True(((FakeHotkeySource)hotkeys).Started);
        Assert.True(((FakeWmiEventSource)wmi).Started);
    }
}
