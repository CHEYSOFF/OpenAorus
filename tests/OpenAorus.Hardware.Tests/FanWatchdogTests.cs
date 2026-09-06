using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The last line: while the app is running, a machine that gets to
/// <see cref="FanSafety.WatchdogTriggerTemperature"/> °C with the fans doing little gets Turbo
/// whether the owner asked for it or not.
/// </summary>
/// <remarks>
/// Two things make this harder than the one-line rule suggests, and both are what the tests
/// below are for. The poll runs about once a second, so firing on the condition rather than on
/// its edge would re-send a seven-step write sequence every second for as long as the machine
/// stayed hot. And a failed sensor read surfaces as 0 °C, so a naive reading of "is it cool
/// again?" would treat a broken sensor as permission to stand down.
/// </remarks>
public class FanWatchdogTests
{
    [Fact]
    public void Fires_when_the_cpu_is_at_the_trigger_and_the_fans_are_not_keeping_up()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(FanSafety.WatchdogTriggerTemperature, 30));
        Assert.True(w.HasFired);
    }

    [Fact]
    public void Does_not_fire_one_degree_below_the_trigger()
    {
        var w = new FanWatchdog();
        Assert.False(w.Observe(FanSafety.WatchdogTriggerTemperature - 1, 0));
        Assert.False(w.HasFired);
    }

    [Fact]
    public void Does_not_fire_when_the_fans_are_already_working()
    {
        // Gaming and Turbo are already up here, which is why the rule needs no mode check.
        var w = new FanWatchdog();
        Assert.False(w.Observe(99, FanSafety.WatchdogDutyFloor));
        Assert.False(w.Observe(99, 100));
        Assert.False(w.HasFired);
    }

    [Fact]
    public void Fires_once_and_then_stays_quiet_while_the_machine_is_still_hot()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));
        // The write takes about three seconds and the duty read-back lags it, so the next few
        // polls still look exactly like the one that fired. None of them may fire again.
        Assert.False(w.Observe(95, 10));
        Assert.False(w.Observe(99, 10));
        Assert.False(w.Observe(92, 10));
    }

    [Fact]
    public void Stays_latched_between_the_rearm_point_and_the_trigger()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));
        Assert.False(w.Observe(FanSafety.WatchdogRearmTemperature, 10));
        Assert.False(w.Observe(FanSafety.WatchdogRearmTemperature + 4, 10));
        Assert.True(w.HasFired);
    }

    [Fact]
    public void Rearms_once_the_cpu_drops_back_below_the_rearm_point_and_can_fire_again()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));
        Assert.False(w.Observe(FanSafety.WatchdogRearmTemperature - 1, 10));
        Assert.False(w.HasFired);
        Assert.True(w.Observe(92, 10));
    }

    [Theory]
    [InlineData(0)]     // what a failed getCpuTemp reads as
    [InlineData(-3)]
    [InlineData(255)]
    public void An_implausible_reading_never_fires(int celsius)
    {
        var w = new FanWatchdog();
        Assert.False(w.Observe(celsius, 0));
        Assert.False(w.HasFired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void An_implausible_reading_is_not_treated_as_the_machine_having_cooled(int celsius)
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));

        // A sensor that starts failing must not quietly re-arm the watchdog: if it did, the
        // next real reading over the trigger would fire a second full write sequence.
        Assert.False(w.Observe(celsius, 10));
        Assert.True(w.HasFired);
        Assert.False(w.Observe(95, 10));
    }

    [Fact]
    public void A_fresh_watchdog_has_not_fired()
    {
        Assert.False(new FanWatchdog().HasFired);
    }
}
