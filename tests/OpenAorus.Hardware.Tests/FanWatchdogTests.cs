using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The last line: while the app is running, a machine that stays at
/// <see cref="FanSafety.WatchdogTriggerTemperature"/> °C with the fans doing little has its fans
/// taken off the owner, whether the owner asked for it or not - and gets them back when it is
/// demonstrably cool again.
/// </summary>
/// <remarks>
/// Four things make this harder than the one-line rule suggests, and all four are what the tests
/// below are for. The poll runs about once a second, so firing on the condition rather than on its
/// edge would re-send a seven-step write sequence every second for as long as the machine stayed
/// hot. A failed sensor read surfaces as 0 °C, so a naive reading of "is it cool again?" would
/// treat a broken sensor as permission to stand down. The response is two stages deep -
/// <see cref="FanSafety.WatchdogFirstStageMode"/> first, then
/// <see cref="FanSafety.WatchdogLastResortMode"/> - so the latch has to hold a stage rather than a
/// bit, and has to let exactly one escalation through without letting a second one past it. And
/// both ends of the emergency are now counted rather than instantaneous:
/// <see cref="FanSafety.WatchdogPollsToFire"/> qualifying polls to engage,
/// <see cref="FanSafety.WatchdogPollsToRelease"/> cool ones to let go, deliberately asymmetric so
/// the two cannot chase each other.
/// </remarks>
public class FanWatchdogTests
{
    /// <summary>Hot enough to qualify, and not the trigger itself, so the tests do not all read as
    /// boundary cases.</summary>
    private const int Hot = 97;

    /// <summary>Clearly below the re-arm point, which is what the release run counts.</summary>
    private const int Cool = FanSafety.WatchdogRearmTemperature - 5;

    /// <summary>A duty under <see cref="FanSafety.WatchdogDutyFloor"/>: fans not keeping up.</summary>
    private const int SlowDuty = 10;

    /// <summary>Drives polls that must do nothing, asserting that each of them does nothing.</summary>
    private static void Quiet(FanWatchdog w, int polls, int cpu, int duty)
    {
        for (var i = 0; i < polls; i++)
            Assert.Equal(WatchdogAction.None, w.Observe(cpu, duty));
    }

    /// <summary>Runs the machine hot for exactly as long as it takes, and returns the poll that acts.</summary>
    private static WatchdogAction RunHotUntilItActs(FanWatchdog w, int cpu = Hot, int duty = SlowDuty)
    {
        Quiet(w, FanSafety.WatchdogPollsToFire - 1, cpu, duty);
        return w.Observe(cpu, duty);
    }

    /// <summary>Cools the machine for exactly as long as it takes, and returns the poll that acts.</summary>
    private static WatchdogAction RunCoolUntilItActs(FanWatchdog w, int cpu = Cool)
    {
        Quiet(w, FanSafety.WatchdogPollsToRelease - 1, cpu, 100);
        return w.Observe(cpu, 100);
    }

    // ---- engaging ---------------------------------------------------------------------------

    [Fact]
    public void Fires_once_the_cpu_has_stayed_at_the_trigger_with_the_fans_not_keeping_up()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force,
            RunHotUntilItActs(w, FanSafety.WatchdogTriggerTemperature, SlowDuty));
        Assert.True(w.HasFired);
    }

    [Fact]
    public void A_momentary_spike_does_nothing_at_all()
    {
        var w = new FanWatchdog();

        // The reason the count exists. This CPU boosts into the 90s constantly and the
        // controller's own table already runs the fans flat out up there, so one qualifying
        // reading is not evidence of anything - it is Tuesday.
        Quiet(w, FanSafety.WatchdogPollsToFire - 1, 110, SlowDuty);
        Assert.False(w.HasFired);

        // And the run has to be consecutive: one cool poll throws the evidence away.
        Assert.Equal(WatchdogAction.None, w.Observe(Cool, SlowDuty));
        Quiet(w, FanSafety.WatchdogPollsToFire - 1, 110, SlowDuty);
        Assert.False(w.HasFired);
    }

    [Fact]
    public void Does_not_fire_one_degree_below_the_trigger()
    {
        var w = new FanWatchdog();
        Quiet(w, FanSafety.WatchdogPollsToFire * 4, FanSafety.WatchdogTriggerTemperature - 1, 0);
        Assert.False(w.HasFired);
    }

    [Fact]
    public void Does_not_fire_when_the_fans_are_already_working()
    {
        // Gaming and Turbo are already up here, which is why the rule needs no mode check.
        var w = new FanWatchdog();
        Quiet(w, FanSafety.WatchdogPollsToFire * 4, 99, FanSafety.WatchdogDutyFloor);
        Quiet(w, FanSafety.WatchdogPollsToFire * 4, 99, 100);
        Assert.False(w.HasFired);
    }

    [Fact]
    public void Fires_once_and_then_stays_quiet_while_the_machine_is_still_hot()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));

        // The write takes about three seconds and the duty read-back lags it, so the next few
        // polls still look exactly like the one that fired. None of them may fire again.
        Assert.Equal(WatchdogAction.None, w.Observe(Hot, SlowDuty));
        Assert.Equal(WatchdogAction.None, w.Observe(99, SlowDuty));
        Assert.Equal(WatchdogAction.None, w.Observe(Hot, SlowDuty));
    }

    [Fact]
    public void Stays_latched_between_the_rearm_point_and_the_trigger()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        Quiet(w, FanSafety.WatchdogPollsToRelease * 2, FanSafety.WatchdogRearmTemperature, SlowDuty);
        Quiet(w, FanSafety.WatchdogPollsToRelease * 2, FanSafety.WatchdogRearmTemperature + 4, SlowDuty);
        Assert.True(w.HasFired);
    }

    [Theory]
    [InlineData(0)]     // what a failed getCpuTemp reads as
    [InlineData(-3)]
    [InlineData(255)]
    public void An_implausible_reading_never_fires(int celsius)
    {
        var w = new FanWatchdog();
        Quiet(w, FanSafety.WatchdogPollsToFire * 4, celsius, 0);
        Assert.False(w.HasFired);
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
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: false);

        // Too hot and no extra cooling: the one state where giving up would be worst. The count
        // is not reset either - the machine has already proved it is sustained - so the retry is
        // the very next poll rather than five seconds later. It goes back to the first stage
        // rather than on to the last resort, because the first stage is what never reached the
        // controller; there is nothing yet to escalate away from.
        Assert.False(w.HasFired);
        Assert.Equal(WatchdogAction.Force, w.Observe(Hot, SlowDuty));
        Assert.Equal(FanSafety.WatchdogFirstStageMode, w.ModeToApply);
    }

    [Fact]
    public void A_mode_applied_by_anyone_else_re_arms_it_without_waiting_for_the_machine_to_cool()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);

        // A resume, an --apply or the owner picking a mode: what the watchdog forced has been
        // overridden, so the only thing the latch was protecting is gone.
        w.NoteModeApplied();

        Assert.False(w.HasFired);
        Assert.Equal(WatchdogAction.Force, w.Observe(Hot, SlowDuty));
    }

    // ---- the escalation -------------------------------------------------------------------

    [Fact]
    public void The_first_fire_asks_for_the_first_stage_and_not_the_last_resort()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));

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
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);     // the first stage reached the controller

        // Still at the trigger, still below the duty floor: the measured duty says the first
        // stage did not lift the fans, so the last resort follows on the next poll. The sustained
        // count gates the start of an emergency, not this - the machine has already spent five
        // polls proving itself and then had a whole write sequence land on it.
        Assert.Equal(WatchdogAction.Force, w.Observe(Hot, SlowDuty));
        Assert.Equal(FanSafety.WatchdogLastResortMode, w.ModeToApply);
    }

    [Fact]
    public void A_first_stage_that_did_lift_the_fans_is_not_escalated()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);

        // This is the whole reason for staging: the machine is still hot, but the fans are now
        // doing the work, so there is nothing to gain from pinning them at maximum.
        Assert.Equal(WatchdogAction.None, w.Observe(99, FanSafety.WatchdogDutyFloor));
        Assert.Equal(WatchdogAction.None, w.Observe(99, 100));
        Assert.Equal(FanSafety.WatchdogFirstStageMode, w.ModeToApply);
    }

    [Fact]
    public void A_poll_arriving_while_the_first_stage_is_still_being_written_does_not_escalate()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));

        // No NoteForcedMode yet: the five-step sequence is still going out at 500 ms a step, so
        // these polls read a duty that predates it. Escalating on them would queue the last
        // resort on top of a first stage that has not had a chance to do anything.
        Assert.Equal(WatchdogAction.None, w.Observe(Hot, SlowDuty));
        Assert.Equal(WatchdogAction.None, w.Observe(99, SlowDuty));
    }

    [Fact]
    public void The_last_resort_is_the_end_of_the_escalation()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Force, w.Observe(Hot, SlowDuty));
        w.NoteForcedMode(applied: true);

        // There is nothing above maximum, so from here the latch behaves exactly as it did before
        // the escalation existed: hot polls change nothing until the machine cools.
        Quiet(w, 30, Hot, SlowDuty);
        Assert.Equal(FanSafety.WatchdogLastResortMode, w.ModeToApply);
    }

    [Fact]
    public void The_whole_escalation_costs_two_write_sequences_and_no_more()
    {
        var w = new FanWatchdog();
        var fires = 0;

        // A minute of a machine sitting at 97 °C with the fans refusing to come up. The stages
        // are the only two things allowed to reach the controller in that time.
        for (var poll = 0; poll < 60; poll++)
        {
            if (w.Observe(Hot, SlowDuty) == WatchdogAction.Force) { fires++; w.NoteForcedMode(applied: true); }
        }

        Assert.Equal(2, fires);
    }

    // ---- letting go -------------------------------------------------------------------------

    [Fact]
    public void Releases_once_the_cpu_has_stayed_below_the_rearm_point()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);

        Assert.Equal(WatchdogAction.Release, RunCoolUntilItActs(w));

        // Release is a hand-back, not a stage: there is nothing of the watchdog's own left to
        // apply, and the next emergency starts from the beginning.
        Assert.False(w.HasFired);
        Assert.Null(w.ModeToApply);
    }

    [Fact]
    public void The_last_resort_is_released_too()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Force, w.Observe(Hot, SlowDuty));
        w.NoteForcedMode(applied: true);

        // Turbo is where the owner's complaint lives: it ignores the temperature, so without this
        // the fans stay at full at 60 °C until someone changes the mode by hand.
        Assert.Equal(WatchdogAction.Release, RunCoolUntilItActs(w));
        Assert.False(w.HasFired);
    }

    [Fact]
    public void A_brief_dip_below_the_rearm_point_does_not_release()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);

        // Fifteen polls to let go against five to engage is the asymmetry that stops this
        // oscillating: a machine dipping in and out of the danger zone can never satisfy both.
        Quiet(w, FanSafety.WatchdogPollsToRelease - 1, Cool, 100);
        Assert.Equal(WatchdogAction.None, w.Observe(Hot, 100));
        Quiet(w, FanSafety.WatchdogPollsToRelease - 1, Cool, 100);
        Assert.True(w.HasFired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void An_implausible_reading_is_not_treated_as_the_machine_having_cooled(int celsius)
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);

        // A sensor that starts failing must not quietly release the watchdog: if it did, the
        // fans would be handed back on the strength of a reading that is not a temperature.
        Quiet(w, FanSafety.WatchdogPollsToRelease * 3, celsius, 10);
        Assert.True(w.HasFired);

        // And the machine is still where it was, so the escalation is still available.
        Assert.Equal(WatchdogAction.Force, w.Observe(Hot, SlowDuty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void An_implausible_reading_breaks_the_run_of_cool_ones(int celsius)
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);

        Quiet(w, FanSafety.WatchdogPollsToRelease - 1, Cool, 100);
        Assert.Equal(WatchdogAction.None, w.Observe(celsius, 100));   // not evidence of anything
        Quiet(w, FanSafety.WatchdogPollsToRelease - 1, Cool, 100);
        Assert.True(w.HasFired);
        Assert.Equal(WatchdogAction.Release, w.Observe(Cool, 100));
    }

    [Fact]
    public void A_release_cannot_be_followed_by_another_one()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Release, RunCoolUntilItActs(w));

        // Releasing means writing the owner's mode, and that write re-arms the watchdog through
        // NoteModeApplied. Both of those land on a watchdog that is already back at None, and a
        // release is only ever offered from a stage - so a cool machine cannot be handed back to
        // its owner over and over.
        w.NoteModeApplied();
        Quiet(w, FanSafety.WatchdogPollsToRelease * 4, Cool, 100);
        Assert.False(w.HasFired);
    }

    [Fact]
    public void Releasing_and_then_getting_hot_again_starts_the_next_emergency_at_the_first_stage()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Force, w.Observe(Hot, SlowDuty));
        w.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Release, RunCoolUntilItActs(w));
        w.NoteModeApplied();

        // A new emergency, so it has to prove itself again from scratch and start at the stage
        // that still tracks temperature.
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        Assert.Equal(FanSafety.WatchdogFirstStageMode, w.ModeToApply);
    }

    [Fact]
    public void A_mode_applied_from_elsewhere_starts_the_next_emergency_at_the_first_stage_again()
    {
        var w = new FanWatchdog();
        Assert.Equal(WatchdogAction.Force, RunHotUntilItActs(w));
        w.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Force, w.Observe(Hot, SlowDuty));
        w.NoteForcedMode(applied: true);

        w.NoteModeApplied();                // a resume, an --apply, or the owner picking a mode

        Assert.Equal(WatchdogAction.Force, w.Observe(Hot, SlowDuty));
        Assert.Equal(FanSafety.WatchdogFirstStageMode, w.ModeToApply);
    }

    [Fact]
    public void One_hot_spell_that_cools_off_costs_two_forced_writes_and_one_hand_back()
    {
        var w = new FanWatchdog();
        var forced = 0;
        var released = 0;

        // Half a minute at 97 °C with the fans refusing to come up, then two minutes idling at
        // 75 °C. Three write sequences reach the controller in all of that, and no more.
        for (var poll = 0; poll < 30; poll++)
        {
            if (w.Observe(Hot, SlowDuty) == WatchdogAction.Force) { forced++; w.NoteForcedMode(applied: true); }
        }
        for (var poll = 0; poll < 120; poll++)
        {
            if (w.Observe(Cool, 100) == WatchdogAction.Release) { released++; w.NoteModeApplied(); }
        }

        Assert.Equal(2, forced);
        Assert.Equal(1, released);
    }
}
