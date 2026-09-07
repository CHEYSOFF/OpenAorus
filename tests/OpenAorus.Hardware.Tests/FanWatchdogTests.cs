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
/// <para>
/// Five things make this harder than the one-line rule suggests, and all five are what the tests
/// below are for. The poll runs on a timer, so firing on the condition rather than on its edge
/// would re-send a seven-step write sequence every tick for as long as the machine stayed hot. A
/// failed sensor read surfaces as 0 °C, so a naive reading of "is it cool again?" would treat a
/// broken sensor as permission to stand down. The response is two stages deep -
/// <see cref="FanSafety.WatchdogFirstStageMode"/> first, then
/// <see cref="FanSafety.WatchdogLastResortMode"/> - so the latch has to hold a stage rather than a
/// bit, and has to let exactly one escalation through without letting a second one past it.
/// </para>
/// <para>
/// And both ends of the emergency are runs rather than instants: <see cref="FanSafety.WatchdogSecondsToFire"/>
/// seconds to engage, <see cref="FanSafety.WatchdogSecondsToRelease"/> to let go, deliberately
/// asymmetric so the two cannot chase each other. They are measured in seconds and not in polls
/// because the poll rate is not fixed - it is five times slower with the window in the tray - so a
/// count of polls silently meant five different things depending on where the window was. Every
/// run below is therefore driven through <see cref="Rig"/>, at more than one rate, and the rate is
/// changed underneath a run on purpose.
/// </para>
/// <para>
/// The fifth is the one a duration adds: two readings far apart span the required seconds while
/// saying nothing about the time between them. A suspended machine produces exactly that, and it
/// must not count as sustained.
/// </para>
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

    /// <summary>The rate the app polls at with its window open.</summary>
    private const int VisibleIntervalMs = 1000;

    /// <summary>The rate it polls at with the window in the tray, which is where a background
    /// utility spends its life and where the poll-counting version of this took five times as
    /// long to do anything.</summary>
    private const int HiddenIntervalMs = 5000;

    /// <summary>
    /// A watchdog, the clock its caller reads, and the cadence that caller is polling at.
    /// </summary>
    /// <remarks>The watchdog has no clock of its own - that is the point of it - so this is the
    /// only time there is, and it is wound on by hand. Nothing here sleeps, so a run of fifteen
    /// seconds costs no wall-clock time at all and cannot be flaky.</remarks>
    private sealed class Rig
    {
        public FanWatchdog Watchdog { get; } = new();

        /// <summary>The monotonic clock handed to <see cref="FanWatchdog.Observe"/>.</summary>
        public long NowMs { get; private set; }

        /// <summary>The interval polls arrive at, and the one the watchdog is told to expect.
        /// Assigning it mid-run is how the window opening and closing is staged.</summary>
        public int IntervalMs { get; set; } = VisibleIntervalMs;

        /// <summary>One poll, one interval after the last.</summary>
        public WatchdogAction Poll(int cpu, int duty)
        {
            NowMs += IntervalMs;
            return Watchdog.Observe(cpu, duty, NowMs, IntervalMs);
        }

        /// <summary>One poll that arrives <paramref name="gapMs"/> after the last instead of one
        /// interval after it: a skipped tick, a suspended machine, or a clock that jumped.</summary>
        public WatchdogAction PollAfter(long gapMs, int cpu, int duty)
        {
            NowMs += gapMs;
            return Watchdog.Observe(cpu, duty, NowMs, IntervalMs);
        }

        /// <summary>Polls on cadence for <paramref name="seconds"/> of machine time, asserting that
        /// every one of them does nothing.</summary>
        public void QuietFor(int seconds, int cpu, int duty)
        {
            var untilMs = NowMs + seconds * 1000L;
            while (NowMs < untilMs)
                Assert.Equal(WatchdogAction.None, Poll(cpu, duty));
        }

        /// <summary>Polls on cadence, asserting each does nothing, and stops one poll short of the
        /// one that would carry a run started at <paramref name="runStartedMs"/> past
        /// <paramref name="seconds"/>.</summary>
        public void QuietUntilJustBefore(long runStartedMs, int seconds, int cpu, int duty)
        {
            while (NowMs + IntervalMs - runStartedMs < seconds * 1000L)
                Assert.Equal(WatchdogAction.None, Poll(cpu, duty));
        }

        /// <summary>The same, and then the poll that crosses the line - the one allowed to act.</summary>
        public WatchdogAction RunUntilItActs(long runStartedMs, int seconds, int cpu, int duty)
        {
            QuietUntilJustBefore(runStartedMs, seconds, cpu, duty);
            return Poll(cpu, duty);
        }

        /// <summary>Runs the machine hot for exactly as long as it takes, and returns the poll that
        /// acts. The run starts at the next poll, whenever that lands.</summary>
        public WatchdogAction RunHotUntilItActs(int cpu = Hot, int duty = SlowDuty) =>
            RunUntilItActs(NowMs + IntervalMs, FanSafety.WatchdogSecondsToFire, cpu, duty);

        /// <summary>Cools the machine for exactly as long as it takes, and returns the poll that
        /// acts.</summary>
        public WatchdogAction RunCoolUntilItActs(int cpu = Cool) =>
            RunUntilItActs(NowMs + IntervalMs, FanSafety.WatchdogSecondsToRelease, cpu, 100);
    }

    private static long FireMs => FanSafety.WatchdogSecondsToFire * 1000L;
    private static long ReleaseMs => FanSafety.WatchdogSecondsToRelease * 1000L;

    // ---- the run is a duration, at any poll rate ----------------------------------------------

    [Theory]
    [InlineData(VisibleIntervalMs)]
    [InlineData(2000)]
    [InlineData(HiddenIntervalMs)]
    public void The_run_to_engage_is_the_same_five_seconds_however_fast_the_app_is_polling(int intervalMs)
    {
        var rig = new Rig { IntervalMs = intervalMs };
        var startedMs = rig.NowMs + intervalMs;   // the first hot poll starts the run

        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());

        // The defect this replaced counted polls, so at the tray's five-second poll it took
        // twenty-five seconds to answer a machine that was cooking - the slowest response on the
        // machine nobody is watching. Within one poll of five seconds, whatever the rate.
        Assert.InRange(rig.NowMs - startedMs, FireMs, FireMs + intervalMs - 1);
    }

    [Theory]
    [InlineData(VisibleIntervalMs)]
    [InlineData(2000)]
    [InlineData(HiddenIntervalMs)]
    public void The_run_to_let_go_is_the_same_fifteen_seconds_however_fast_the_app_is_polling(int intervalMs)
    {
        var rig = new Rig { IntervalMs = intervalMs };
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);

        var startedMs = rig.NowMs + intervalMs;
        Assert.Equal(WatchdogAction.Release, rig.RunCoolUntilItActs());

        // The owner's report, in one assertion: they waited "fifteen or more seconds" for the fans
        // to come down and were actually waiting on seventy-five, because the window was in the
        // tray. Fifteen seconds means fifteen seconds wherever the window is.
        Assert.InRange(rig.NowMs - startedMs, ReleaseMs, ReleaseMs + intervalMs - 1);
    }

    [Fact]
    public void A_hot_run_that_starts_in_the_tray_and_ends_with_the_window_open_is_not_restarted()
    {
        var rig = new Rig { IntervalMs = HiddenIntervalMs };
        var startedMs = rig.NowMs + HiddenIntervalMs;
        Assert.Equal(WatchdogAction.None, rig.Poll(Hot, SlowDuty));

        // The owner opens the window while the machine is hot, so the poller speeds up in the
        // middle of the run. Elapsed time does not care: the run simply continues, and five
        // seconds after it began - not five seconds after the rate changed - it fires.
        rig.IntervalMs = VisibleIntervalMs;
        Assert.Equal(WatchdogAction.Force, rig.RunUntilItActs(startedMs, FanSafety.WatchdogSecondsToFire, Hot, SlowDuty));
        Assert.Equal(FireMs, rig.NowMs - startedMs);
    }

    [Fact]
    public void A_hot_run_that_starts_with_the_window_open_and_ends_in_the_tray_is_not_restarted()
    {
        var rig = new Rig { IntervalMs = VisibleIntervalMs };
        var startedMs = rig.NowMs + VisibleIntervalMs;
        Assert.Equal(WatchdogAction.None, rig.Poll(Hot, SlowDuty));
        Assert.Equal(WatchdogAction.None, rig.Poll(Hot, SlowDuty));

        // And the other way round: the window is hidden a second into the emergency and the polls
        // drop to one every five seconds. The next one is late by the old cadence and ordinary by
        // the new one, so it extends the run rather than breaking it.
        rig.IntervalMs = HiddenIntervalMs;
        Assert.Equal(WatchdogAction.Force, rig.Poll(Hot, SlowDuty));
        Assert.InRange(rig.NowMs - startedMs, FireMs, FireMs + HiddenIntervalMs - 1);
    }

    [Fact]
    public void A_cool_run_that_starts_in_the_tray_and_ends_with_the_window_open_is_not_restarted()
    {
        var rig = new Rig { IntervalMs = HiddenIntervalMs };
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);

        var startedMs = rig.NowMs + HiddenIntervalMs;
        Assert.Equal(WatchdogAction.None, rig.Poll(Cool, 100));

        // The machine cools while the app is in the tray and the owner opens the window to look at
        // it. Their fans come back fifteen seconds after it cooled, not fifteen seconds after they
        // looked.
        rig.IntervalMs = VisibleIntervalMs;
        Assert.Equal(WatchdogAction.Release, rig.RunUntilItActs(startedMs, FanSafety.WatchdogSecondsToRelease, Cool, 100));
        Assert.Equal(ReleaseMs, rig.NowMs - startedMs);
    }

    [Fact]
    public void A_cool_run_that_starts_with_the_window_open_and_ends_in_the_tray_is_not_restarted()
    {
        var rig = new Rig { IntervalMs = VisibleIntervalMs };
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);

        var startedMs = rig.NowMs + VisibleIntervalMs;
        Assert.Equal(WatchdogAction.None, rig.Poll(Cool, 100));
        rig.IntervalMs = HiddenIntervalMs;

        // The owner puts the window away as the machine cools. The run keeps counting across the
        // change, and lands within one of the new, slower polls of the fifteen seconds.
        Assert.Equal(WatchdogAction.Release, rig.RunUntilItActs(startedMs, FanSafety.WatchdogSecondsToRelease, Cool, 100));
        Assert.InRange(rig.NowMs - startedMs, ReleaseMs, ReleaseMs + HiddenIntervalMs - 1);
    }

    // ---- a run has to be observed, not just spanned -------------------------------------------

    [Fact]
    public void Two_hot_readings_far_apart_are_not_a_sustained_hot_machine()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.None, rig.Poll(Hot, SlowDuty));

        // The app was suspended, or the UI thread was starved, or the tick was simply dropped.
        // A minute passed and exactly two readings came out of it. That spans five seconds many
        // times over and says nothing whatever about the minute in between, so it may not force
        // anything on its own.
        Assert.Equal(WatchdogAction.None, rig.PollAfter(60_000, Hot, SlowDuty));
        Assert.False(rig.Watchdog.HasFired);

        // The run restarts from that poll rather than being latched off - it is still a hot
        // reading, it is just not five seconds of them yet.
        Assert.Equal(WatchdogAction.Force,
            rig.RunUntilItActs(rig.NowMs, FanSafety.WatchdogSecondsToFire, Hot, SlowDuty));
    }

    [Fact]
    public void Two_cool_readings_far_apart_are_not_a_machine_that_has_stayed_cool()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);

        Assert.Equal(WatchdogAction.None, rig.Poll(Cool, 100));

        // Same shape on the other end, and the same answer. A machine that was asleep was not
        // being watched, and handing the fans back is a claim about what happened while nobody
        // was looking.
        Assert.Equal(WatchdogAction.None, rig.PollAfter(60_000, Cool, 100));
        Assert.True(rig.Watchdog.HasFired);

        Assert.Equal(WatchdogAction.Release,
            rig.RunUntilItActs(rig.NowMs, FanSafety.WatchdogSecondsToRelease, Cool, 100));
    }

    [Fact]
    public void A_poll_that_is_merely_a_little_late_does_not_break_the_run()
    {
        var rig = new Rig();
        var startedMs = rig.NowMs + VisibleIntervalMs;
        Assert.Equal(WatchdogAction.None, rig.Poll(Hot, SlowDuty));

        // A busy machine delivers a tick late, and this one runs on the UI thread of an app that
        // is also writing to the controller. Being strict here would mean the guard quietly
        // switching itself off exactly when the machine is working hardest.
        Assert.Equal(WatchdogAction.None,
            rig.PollAfter(VisibleIntervalMs * FanSafety.WatchdogMaxPollGapFactor, Hot, SlowDuty));

        Assert.Equal(WatchdogAction.Force, rig.RunUntilItActs(startedMs, FanSafety.WatchdogSecondsToFire, Hot, SlowDuty));
        Assert.Equal(FireMs, rig.NowMs - startedMs);
    }

    [Fact]
    public void A_clock_that_jumps_backwards_breaks_the_run_rather_than_completing_it()
    {
        var rig = new Rig();
        rig.QuietUntilJustBefore(rig.NowMs + VisibleIntervalMs, FanSafety.WatchdogSecondsToFire, Hot, SlowDuty);

        // Monotonic clocks are not supposed to do this, and the machine is not supposed to be at
        // 97 °C either. Erring towards not acting is the only safe way round: a run measured
        // against a clock that moved is not a measurement.
        Assert.Equal(WatchdogAction.None, rig.PollAfter(-60_000, Hot, SlowDuty));
        Assert.False(rig.Watchdog.HasFired);

        Assert.Equal(WatchdogAction.Force,
            rig.RunUntilItActs(rig.NowMs, FanSafety.WatchdogSecondsToFire, Hot, SlowDuty));
    }

    // ---- engaging ---------------------------------------------------------------------------

    [Fact]
    public void Fires_once_the_cpu_has_stayed_at_the_trigger_with_the_fans_not_keeping_up()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force,
            rig.RunHotUntilItActs(FanSafety.WatchdogTriggerTemperature, SlowDuty));
        Assert.True(rig.Watchdog.HasFired);
    }

    [Fact]
    public void A_momentary_spike_does_nothing_at_all()
    {
        var rig = new Rig();

        // The reason the run exists. This CPU boosts into the 90s constantly and the controller's
        // own table already runs the fans flat out up there, so a few qualifying readings are not
        // evidence of anything - they are Tuesday.
        rig.QuietUntilJustBefore(rig.NowMs + VisibleIntervalMs, FanSafety.WatchdogSecondsToFire, 110, SlowDuty);
        Assert.False(rig.Watchdog.HasFired);

        // And the run has to be unbroken: one cool poll throws the evidence away.
        Assert.Equal(WatchdogAction.None, rig.Poll(Cool, SlowDuty));
        rig.QuietUntilJustBefore(rig.NowMs + VisibleIntervalMs, FanSafety.WatchdogSecondsToFire, 110, SlowDuty);
        Assert.False(rig.Watchdog.HasFired);
    }

    [Fact]
    public void Does_not_fire_one_degree_below_the_trigger()
    {
        var rig = new Rig();
        rig.QuietFor(FanSafety.WatchdogSecondsToFire * 4, FanSafety.WatchdogTriggerTemperature - 1, 0);
        Assert.False(rig.Watchdog.HasFired);
    }

    [Fact]
    public void Does_not_fire_when_the_fans_are_already_working()
    {
        // Gaming and Turbo are already up here, which is why the rule needs no mode check.
        var rig = new Rig();
        rig.QuietFor(FanSafety.WatchdogSecondsToFire * 4, 99, FanSafety.WatchdogDutyFloor);
        rig.QuietFor(FanSafety.WatchdogSecondsToFire * 4, 99, 100);
        Assert.False(rig.Watchdog.HasFired);
    }

    [Fact]
    public void Fires_once_and_then_stays_quiet_while_the_machine_is_still_hot()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());

        // The write takes about three seconds and the duty read-back lags it, so the next few
        // polls still look exactly like the one that fired. None of them may fire again.
        Assert.Equal(WatchdogAction.None, rig.Poll(Hot, SlowDuty));
        Assert.Equal(WatchdogAction.None, rig.Poll(99, SlowDuty));
        Assert.Equal(WatchdogAction.None, rig.Poll(Hot, SlowDuty));
    }

    [Fact]
    public void Stays_latched_between_the_rearm_point_and_the_trigger()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.QuietFor(FanSafety.WatchdogSecondsToRelease * 2, FanSafety.WatchdogRearmTemperature, SlowDuty);
        rig.QuietFor(FanSafety.WatchdogSecondsToRelease * 2, FanSafety.WatchdogRearmTemperature + 4, SlowDuty);
        Assert.True(rig.Watchdog.HasFired);
    }

    [Theory]
    [InlineData(0)]     // what a failed getCpuTemp reads as
    [InlineData(-3)]
    [InlineData(255)]
    public void An_implausible_reading_never_fires(int celsius)
    {
        var rig = new Rig();
        rig.QuietFor(FanSafety.WatchdogSecondsToFire * 4, celsius, 0);
        Assert.False(rig.Watchdog.HasFired);
    }

    [Fact]
    public void A_fresh_watchdog_has_not_fired()
    {
        Assert.False(new FanWatchdog().HasFired);
    }

    [Fact]
    public void A_forced_mode_that_failed_leaves_it_armed_to_try_again()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: false);

        // Too hot and no extra cooling: the one state where giving up would be worst. The run is
        // not thrown away either - the machine has already proved it is sustained - so the retry
        // is the very next poll rather than five seconds later. It goes back to the first stage
        // rather than on to the last resort, because the first stage is what never reached the
        // controller; there is nothing yet to escalate away from.
        Assert.False(rig.Watchdog.HasFired);
        Assert.Equal(WatchdogAction.Force, rig.Poll(Hot, SlowDuty));
        Assert.Equal(FanSafety.WatchdogFirstStageMode, rig.Watchdog.ModeToApply);
    }

    [Fact]
    public void A_mode_applied_by_anyone_else_re_arms_it_without_waiting_for_the_machine_to_cool()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);

        // A resume, an --apply or the owner picking a mode: what the watchdog forced has been
        // overridden, so the only thing the latch was protecting is gone.
        rig.Watchdog.NoteModeApplied();

        Assert.False(rig.Watchdog.HasFired);
        Assert.Equal(WatchdogAction.Force, rig.Poll(Hot, SlowDuty));
    }

    // ---- the escalation -------------------------------------------------------------------

    [Fact]
    public void The_first_fire_asks_for_the_first_stage_and_not_the_last_resort()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());

        // Gaming, not Turbo. Turbo pins both fans at DutyMax and, being a fixed-duty mode, stops
        // reading the temperature at all - so it roars until something else changes the mode, even
        // once the machine is cold. The first answer to a hot machine is the aggressive automatic
        // curve, which still tracks temperature.
        Assert.Equal(FanSafety.WatchdogFirstStageMode, rig.Watchdog.ModeToApply);
        Assert.NotEqual(FanSafety.WatchdogLastResortMode, rig.Watchdog.ModeToApply);
    }

    [Fact]
    public void A_fresh_watchdog_is_asking_for_nothing()
    {
        Assert.Null(new FanWatchdog().ModeToApply);
    }

    [Fact]
    public void A_machine_the_first_stage_did_not_cool_escalates_to_the_last_resort()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);     // the first stage reached the controller

        // Still at the trigger, still below the duty floor: the measured duty says the first
        // stage did not lift the fans, so the last resort follows on the next poll. The run gates
        // the start of an emergency, not this - the machine has already spent its five seconds
        // proving itself and then had a whole write sequence land on it.
        Assert.Equal(WatchdogAction.Force, rig.Poll(Hot, SlowDuty));
        Assert.Equal(FanSafety.WatchdogLastResortMode, rig.Watchdog.ModeToApply);
    }

    [Fact]
    public void A_first_stage_that_did_lift_the_fans_is_not_escalated()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);

        // This is the whole reason for staging: the machine is still hot, but the fans are now
        // doing the work, so there is nothing to gain from pinning them at maximum.
        Assert.Equal(WatchdogAction.None, rig.Poll(99, FanSafety.WatchdogDutyFloor));
        Assert.Equal(WatchdogAction.None, rig.Poll(99, 100));
        Assert.Equal(FanSafety.WatchdogFirstStageMode, rig.Watchdog.ModeToApply);
    }

    [Fact]
    public void A_poll_arriving_while_the_first_stage_is_still_being_written_does_not_escalate()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());

        // No NoteForcedMode yet: the five-step sequence is still going out at 500 ms a step, so
        // these polls read a duty that predates it. Escalating on them would queue the last
        // resort on top of a first stage that has not had a chance to do anything.
        Assert.Equal(WatchdogAction.None, rig.Poll(Hot, SlowDuty));
        Assert.Equal(WatchdogAction.None, rig.Poll(99, SlowDuty));
    }

    [Fact]
    public void The_last_resort_is_the_end_of_the_escalation()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Force, rig.Poll(Hot, SlowDuty));
        rig.Watchdog.NoteForcedMode(applied: true);

        // There is nothing above maximum, so from here the latch behaves exactly as it did before
        // the escalation existed: hot polls change nothing until the machine cools.
        rig.QuietFor(30, Hot, SlowDuty);
        Assert.Equal(FanSafety.WatchdogLastResortMode, rig.Watchdog.ModeToApply);
    }

    [Fact]
    public void The_whole_escalation_costs_two_write_sequences_and_no_more()
    {
        var rig = new Rig();
        var fires = 0;

        // A minute of a machine sitting at 97 °C with the fans refusing to come up. The stages
        // are the only two things allowed to reach the controller in that time.
        for (var second = 0; second < 60; second++)
        {
            if (rig.Poll(Hot, SlowDuty) == WatchdogAction.Force)
            {
                fires++;
                rig.Watchdog.NoteForcedMode(applied: true);
            }
        }

        Assert.Equal(2, fires);
    }

    // ---- letting go -------------------------------------------------------------------------

    [Fact]
    public void Releases_once_the_cpu_has_stayed_below_the_rearm_point()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);

        Assert.Equal(WatchdogAction.Release, rig.RunCoolUntilItActs());

        // Release is a hand-back, not a stage: there is nothing of the watchdog's own left to
        // apply, and the next emergency starts from the beginning.
        Assert.False(rig.Watchdog.HasFired);
        Assert.Null(rig.Watchdog.ModeToApply);
    }

    [Fact]
    public void The_last_resort_is_released_too()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Force, rig.Poll(Hot, SlowDuty));
        rig.Watchdog.NoteForcedMode(applied: true);

        // Turbo is where the owner's complaint lives: it ignores the temperature, so without this
        // the fans stay at full at 60 °C until someone changes the mode by hand.
        Assert.Equal(WatchdogAction.Release, rig.RunCoolUntilItActs());
        Assert.False(rig.Watchdog.HasFired);
    }

    [Fact]
    public void A_brief_dip_below_the_rearm_point_does_not_release()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);

        // Fifteen seconds to let go against five to engage is the asymmetry that stops this
        // oscillating: a machine dipping in and out of the danger zone can never satisfy both.
        rig.QuietUntilJustBefore(rig.NowMs + VisibleIntervalMs, FanSafety.WatchdogSecondsToRelease, Cool, 100);
        Assert.Equal(WatchdogAction.None, rig.Poll(Hot, 100));
        rig.QuietUntilJustBefore(rig.NowMs + VisibleIntervalMs, FanSafety.WatchdogSecondsToRelease, Cool, 100);
        Assert.True(rig.Watchdog.HasFired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void An_implausible_reading_is_not_treated_as_the_machine_having_cooled(int celsius)
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);

        // A sensor that starts failing must not quietly release the watchdog: if it did, the
        // fans would be handed back on the strength of a reading that is not a temperature.
        rig.QuietFor(FanSafety.WatchdogSecondsToRelease * 3, celsius, 10);
        Assert.True(rig.Watchdog.HasFired);

        // And the machine is still where it was, so the escalation is still available.
        Assert.Equal(WatchdogAction.Force, rig.Poll(Hot, SlowDuty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void An_implausible_reading_breaks_the_run_of_cool_ones(int celsius)
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);

        rig.QuietUntilJustBefore(rig.NowMs + VisibleIntervalMs, FanSafety.WatchdogSecondsToRelease, Cool, 100);
        Assert.Equal(WatchdogAction.None, rig.Poll(celsius, 100));   // not evidence of anything
        rig.QuietUntilJustBefore(rig.NowMs + VisibleIntervalMs, FanSafety.WatchdogSecondsToRelease, Cool, 100);
        Assert.True(rig.Watchdog.HasFired);
        Assert.Equal(WatchdogAction.Release, rig.Poll(Cool, 100));
    }

    [Fact]
    public void A_release_cannot_be_followed_by_another_one()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Release, rig.RunCoolUntilItActs());

        // Releasing means writing the owner's mode, and that write re-arms the watchdog through
        // NoteModeApplied. Both of those land on a watchdog that is already back at None, and a
        // release is only ever offered from a stage - so a cool machine cannot be handed back to
        // its owner over and over.
        rig.Watchdog.NoteModeApplied();
        rig.QuietFor(FanSafety.WatchdogSecondsToRelease * 4, Cool, 100);
        Assert.False(rig.Watchdog.HasFired);
    }

    [Fact]
    public void Releasing_and_then_getting_hot_again_starts_the_next_emergency_at_the_first_stage()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Force, rig.Poll(Hot, SlowDuty));
        rig.Watchdog.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Release, rig.RunCoolUntilItActs());
        rig.Watchdog.NoteModeApplied();

        // A new emergency, so it has to prove itself again from scratch and start at the stage
        // that still tracks temperature.
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        Assert.Equal(FanSafety.WatchdogFirstStageMode, rig.Watchdog.ModeToApply);
    }

    [Fact]
    public void A_mode_applied_from_elsewhere_starts_the_next_emergency_at_the_first_stage_again()
    {
        var rig = new Rig();
        Assert.Equal(WatchdogAction.Force, rig.RunHotUntilItActs());
        rig.Watchdog.NoteForcedMode(applied: true);
        Assert.Equal(WatchdogAction.Force, rig.Poll(Hot, SlowDuty));
        rig.Watchdog.NoteForcedMode(applied: true);

        rig.Watchdog.NoteModeApplied();     // a resume, an --apply, or the owner picking a mode

        Assert.Equal(WatchdogAction.Force, rig.Poll(Hot, SlowDuty));
        Assert.Equal(FanSafety.WatchdogFirstStageMode, rig.Watchdog.ModeToApply);
    }

    [Fact]
    public void One_hot_spell_that_cools_off_costs_two_forced_writes_and_one_hand_back()
    {
        var rig = new Rig();
        var forced = 0;
        var released = 0;

        // Half a minute at 97 °C with the fans refusing to come up, then two minutes idling at
        // 75 °C. Three write sequences reach the controller in all of that, and no more.
        for (var second = 0; second < 30; second++)
        {
            if (rig.Poll(Hot, SlowDuty) == WatchdogAction.Force)
            {
                forced++;
                rig.Watchdog.NoteForcedMode(applied: true);
            }
        }
        for (var second = 0; second < 120; second++)
        {
            if (rig.Poll(Cool, 100) == WatchdogAction.Release)
            {
                released++;
                rig.Watchdog.NoteModeApplied();
            }
        }

        Assert.Equal(2, forced);
        Assert.Equal(1, released);
    }
}
