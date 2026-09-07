using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The last line: while the app is running, a machine that gets to
/// <see cref="FanSafety.WatchdogTriggerTemperature"/> °C with the fans doing little has its fans
/// taken off the owner, whether the owner asked for it or not.
/// </summary>
/// <remarks>
/// Three things make this harder than the one-line rule suggests, and all three are what the
/// tests below are for. The poll runs about once a second, so firing on the condition rather than
/// on its edge would re-send a seven-step write sequence every second for as long as the machine
/// stayed hot. A failed sensor read surfaces as 0 °C, so a naive reading of "is it cool again?"
/// would treat a broken sensor as permission to stand down. And the response is now two stages
/// deep - <see cref="FanSafety.WatchdogFirstStageMode"/> first, then
/// <see cref="FanSafety.WatchdogLastResortMode"/> - so the latch has to hold a stage rather than
/// a bit, and has to let exactly one escalation through without letting a second one past it.
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

    [Fact]
    public void A_forced_mode_that_failed_leaves_it_armed_to_try_again()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));
        w.NoteForcedMode(applied: false);

        // Too hot and no extra cooling: the one state where giving up would be worst. It goes back
        // to the first stage rather than on to the last resort, because the first stage is what
        // never reached the controller - there is nothing yet to escalate away from.
        Assert.False(w.HasFired);
        Assert.True(w.Observe(92, 10));
        Assert.Equal(FanSafety.WatchdogFirstStageMode, w.ModeToApply);
    }

    [Fact]
    public void A_mode_applied_by_anyone_else_re_arms_it_without_waiting_for_the_machine_to_cool()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));
        w.NoteForcedMode(applied: true);

        // A resume, an --apply or the owner picking a mode: what the watchdog forced has been
        // overridden, so the only thing the latch was protecting is gone.
        w.NoteModeApplied();

        Assert.False(w.HasFired);
        Assert.True(w.Observe(92, 10));
    }

    // ---- the escalation -------------------------------------------------------------------

    [Fact]
    public void The_first_fire_asks_for_the_first_stage_and_not_the_last_resort()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));

        // Gaming, not Turbo. Turbo pins both fans at DutyMax and, being a fixed-duty mode, stops
        // reading the temperature at all - so it roars until something else changes the mode, even
        // once the machine is cold. The first answer to a hot machine is the aggressive automatic
        // curve, which still tracks temperature.
        Assert.Equal(FanSafety.WatchdogFirstStageMode, w.ModeToApply);
        Assert.NotEqual(FanSafety.WatchdogLastResortMode, w.ModeToApply);
    }

    [Fact]
    public void A_fresh_watchdog_is_asking_for_nothing()
    {
        Assert.Null(new FanWatchdog().ModeToApply);
    }

    [Fact]
    public void A_machine_the_first_stage_did_not_cool_escalates_to_the_last_resort()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));
        w.NoteForcedMode(applied: true);     // the first stage reached the controller

        // Still at the trigger, still below the duty floor: the measured duty says the first
        // stage did not lift the fans, so the last resort follows. Nothing here assumes anything
        // about what the controller's aggressive curve does at 90 °C - the reading decides.
        Assert.True(w.Observe(92, 10));
        Assert.Equal(FanSafety.WatchdogLastResortMode, w.ModeToApply);
    }

    [Fact]
    public void A_first_stage_that_did_lift_the_fans_is_not_escalated()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));
        w.NoteForcedMode(applied: true);

        // This is the whole reason for staging: the machine is still hot, but the fans are now
        // doing the work, so there is nothing to gain from pinning them at maximum.
        Assert.False(w.Observe(95, FanSafety.WatchdogDutyFloor));
        Assert.False(w.Observe(97, 100));
        Assert.Equal(FanSafety.WatchdogFirstStageMode, w.ModeToApply);
    }

    [Fact]
    public void A_poll_arriving_while_the_first_stage_is_still_being_written_does_not_escalate()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));

        // No NoteForcedMode yet: the five-step sequence is still going out at 500 ms a step, so
        // these polls read a duty that predates it. Escalating on them would queue the last
        // resort on top of a first stage that has not had a chance to do anything.
        Assert.False(w.Observe(95, 10));
        Assert.False(w.Observe(97, 10));
    }

    [Fact]
    public void The_last_resort_is_the_end_of_the_escalation()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));
        w.NoteForcedMode(applied: true);
        Assert.True(w.Observe(92, 10));
        w.NoteForcedMode(applied: true);

        // There is nothing above maximum, so from here the latch behaves exactly as it did before
        // the escalation existed: hot polls change nothing until the machine cools.
        Assert.False(w.Observe(95, 10));
        Assert.False(w.Observe(99, 10));
        Assert.False(w.Observe(92, 10));
        Assert.Equal(FanSafety.WatchdogLastResortMode, w.ModeToApply);
    }

    [Fact]
    public void Cooling_back_down_starts_the_next_emergency_at_the_first_stage_again()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));
        w.NoteForcedMode(applied: true);
        Assert.True(w.Observe(92, 10));
        w.NoteForcedMode(applied: true);

        Assert.False(w.Observe(FanSafety.WatchdogRearmTemperature - 1, 40));
        Assert.False(w.HasFired);
        Assert.Null(w.ModeToApply);

        Assert.True(w.Observe(92, 10));
        Assert.Equal(FanSafety.WatchdogFirstStageMode, w.ModeToApply);
    }

    [Fact]
    public void A_mode_applied_from_elsewhere_starts_the_next_emergency_at_the_first_stage_again()
    {
        var w = new FanWatchdog();
        Assert.True(w.Observe(92, 10));
        w.NoteForcedMode(applied: true);
        Assert.True(w.Observe(92, 10));
        w.NoteForcedMode(applied: true);

        w.NoteModeApplied();                // a resume, an --apply, or the owner picking a mode

        Assert.True(w.Observe(92, 10));
        Assert.Equal(FanSafety.WatchdogFirstStageMode, w.ModeToApply);
    }

    [Fact]
    public void The_whole_escalation_costs_two_write_sequences_and_no_more()
    {
        var w = new FanWatchdog();
        var fires = 0;

        // Thirty seconds of a machine sitting at 95 °C with the fans refusing to come up. The
        // stages are the only two things allowed to reach the controller in that time.
        for (var poll = 0; poll < 30; poll++)
        {
            if (w.Observe(95, 10)) { fires++; w.NoteForcedMode(applied: true); }
        }

        Assert.Equal(2, fires);
    }
}
