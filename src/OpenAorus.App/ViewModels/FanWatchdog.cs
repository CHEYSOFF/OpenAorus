using OpenAorus.Hardware.Fans;

namespace OpenAorus.App.ViewModels;

/// <summary>How far the watchdog has already gone in answering one hot machine.</summary>
/// <remarks>
/// One emergency runs through these in order and never backwards. Coming back down is not an
/// escalation step: it is the whole state being thrown away and started again from
/// <see cref="None"/>, which is what re-arming means.
/// </remarks>
public enum WatchdogStage
{
    /// <summary>Nothing has been forced; the machine is either cool enough or cooling itself.</summary>
    None,

    /// <summary>The fans have been put on <see cref="FanSafety.WatchdogFirstStageMode"/>.</summary>
    Raised,

    /// <summary>The fans have been pinned by <see cref="FanSafety.WatchdogLastResortMode"/>.</summary>
    Maximum,
}

/// <summary>What one poll asks the owner of this watchdog to do about the machine.</summary>
/// <remarks>
/// A bool would only say "act", and there are now two opposite ways to act - taking the fans off
/// the owner and giving them back - which write different modes and tell the owner different
/// things. Naming them here is what keeps the caller from having to infer which one it got from
/// the stage it happens to be looking at.
/// </remarks>
public enum WatchdogAction
{
    /// <summary>Nothing this poll. By far the usual answer.</summary>
    None,

    /// <summary>Apply <see cref="FanWatchdog.ModeToApply"/> - the stage this poll just moved to.</summary>
    Force,

    /// <summary>Put the mode the <em>owner</em> chose back on: the machine is demonstrably cool
    /// again and the watchdog is no longer holding it.</summary>
    Release,
}

/// <summary>
/// Decides, one sensor poll at a time, whether the machine needs its fans taken off the owner -
/// and, if so, how hard, and for how long.
/// </summary>
/// <remarks>
/// <para>
/// The rule itself is one line - hot CPU, fans not keeping up, override them - but it is fed by a
/// timer, so it is really a small state machine and it lives here on its own rather than inside
/// the poller's callback where nothing could reach it. <see cref="MainViewModel"/> owns one and
/// does the acting; this decides.
/// </para>
/// <para>
/// Both ends of an emergency are runs rather than instants, and the two runs are deliberately
/// different lengths. <see cref="FanSafety.WatchdogSecondsToFire"/> seconds of qualifying readings
/// are needed before anything is forced, because this CPU boosts into the 90s constantly and the
/// controller's own default table already asks for maximum fans up there: a single reading over
/// the trigger is not evidence of a misconfigured machine.
/// <see cref="FanSafety.WatchdogSecondsToRelease"/> seconds below
/// <see cref="FanSafety.WatchdogRearmTemperature"/> °C are needed before the fans are handed back.
/// Three times as long to let go as to engage is what makes oscillation impossible: a machine
/// flickering across the danger zone can satisfy neither run.
/// </para>
/// <para>
/// Both are measured in elapsed time and not in polls, and that is the whole reason this takes a
/// clock reading. The poll rate is not fixed: it is
/// <see cref="OpenAorus.Hardware.Config.AppSettings.PollIntervalVisibleMs"/> with the window open and
/// <see cref="OpenAorus.Hardware.Config.AppSettings.PollIntervalHiddenMs"/> with it in the tray, five times slower,
/// and both are settings. Counting polls therefore meant five seconds to engage with the window
/// open and twenty-five with it away - the slowest response on the machine nobody is watching,
/// which for a tray utility is the ordinary state - and fifteen seconds to let go became
/// seventy-five, which is what the owner was actually waiting on. A duration means the same thing
/// at any rate, including a rate the owner changes, and including a rate that changes in the
/// middle of a run: the window can be opened or hidden while the machine is hot, and a run
/// measured in seconds simply carries on across it.
/// </para>
/// <para>
/// The clock is passed in and never read here. A class that reads its own clock cannot be tested
/// without sleeping, and a test that sleeps is a test that is flaky about the one thing this
/// exists to get right; the same reasoning that keeps <see cref="OpenAorus.Hardware.Hotkeys.SignalDebouncer"/> pure.
/// <see cref="MainViewModel"/> reads <see cref="Environment.TickCount64"/> at the poller's
/// callback and hands it down. Monotonic, so a wall-clock correction cannot invent or erase a run.
/// </para>
/// <para>
/// A duration on its own is not the whole of "sustained", though: two readings an hour apart span
/// five seconds many times over while saying nothing about the hour between them. So the caller
/// also says how often it is polling, and a poll arriving more than
/// <see cref="FanSafety.WatchdogMaxPollGapFactor"/> times that late - a suspended machine, a
/// starved UI thread, a dropped tick - is treated as breaking whatever run it would have extended
/// rather than completing it. Both runs restart from that poll; nothing latches off.
/// </para>
/// <para>
/// The answer comes in two stages, and the reason is that the two available modes are not the
/// same kind of thing. <see cref="FanSafety.WatchdogFirstStageMode"/> is an automatic curve that
/// keeps reading the temperature, so it lifts the fans and then lets them back down on its own.
/// <see cref="FanSafety.WatchdogLastResortMode"/> is a fixed duty: it pins both fans at maximum
/// and stops responding to the sensor entirely, so it keeps roaring long after the machine is
/// cold. Reaching for the second one first buys nothing the first one would not have bought on a
/// machine the first one could cool, and costs the temperature tracking on every machine.
/// </para>
/// <para>
/// What decides between them is the <em>measured</em> duty on a later poll, never an assumption
/// about the first stage's curve - that curve is an unverified part of the recovered protocol, and
/// this is the wrong place to be confident about it. If the fans came up, nothing more happens. If
/// they did not, the last resort follows about a poll later.
/// </para>
/// <para>
/// It is deliberately mode-agnostic in the other direction: nothing here asks what mode the fans
/// are currently on. The duty half of the condition is what keeps it quiet on a machine already
/// cooling itself, so no mode needs naming and nothing has to be kept in step when one is added.
/// </para>
/// <para>
/// The latch only ever exists to stop a second sequence being queued on top of one that is still
/// in force, so it is fed by the two things that end that: <see cref="NoteForcedMode"/>, which
/// says whether the write was ever made at all, and <see cref="NoteModeApplied"/>, which says
/// something else has since overridden it.
/// </para>
/// </remarks>
public sealed class FanWatchdog
{
    /// <summary>How far this watchdog has gone in answering the machine it is currently watching.</summary>
    public WatchdogStage Stage { get; private set; }

    /// <summary>Whether the watchdog has acted and is waiting for the machine to cool, for the
    /// fans to come up, or for the escalation, before it could act again.</summary>
    public bool HasFired => Stage != WatchdogStage.None;

    /// <summary>The mode the stage this watchdog has reached calls for; null when it has not
    /// reached one.</summary>
    /// <remarks>Read by <see cref="MainViewModel"/> on the poll <see cref="Observe"/> answers
    /// <see cref="WatchdogAction.Force"/> for. Which mode belongs to which stage is
    /// <see cref="FanSafety"/>'s to say, so the two names live there beside the temperatures and
    /// the duty floor rather than being spelt out at the call site. It is deliberately null for a
    /// <see cref="WatchdogAction.Release"/>: the mode that goes back on then is the owner's, which
    /// this object has never been told and has no business knowing.</remarks>
    public FanMode? ModeToApply => Stage switch
    {
        WatchdogStage.Raised => FanSafety.WatchdogFirstStageMode,
        WatchdogStage.Maximum => FanSafety.WatchdogLastResortMode,
        _ => null,
    };

    /// <summary>True between the poll that asked for a mode and the outcome of the write it asked
    /// for. That write is itself an applied mode, and it is the one thing that must not re-arm.</summary>
    private bool _forcing;

    /// <summary>An unbroken run of readings that all said the same thing about the machine.</summary>
    /// <remarks>A run is its start and nothing else: how long it has lasted is the current poll's
    /// timestamp minus that, so nothing accumulates, nothing can be capped and nothing drifts.
    /// <see cref="Running"/> being false is a run that has ended - the reading stopped qualifying,
    /// or the polls stopped arriving often enough to be watching.</remarks>
    private struct Run
    {
        public bool Running;
        public long StartedMs;

        /// <summary>Extends the run if <paramref name="qualifies"/>, starting it where it is not
        /// already going; ends it if not.</summary>
        public void Observe(bool qualifies, long nowMs)
        {
            if (!qualifies) { Running = false; return; }
            if (!Running) { Running = true; StartedMs = nowMs; }
        }

        /// <summary>Whether this run has been going for <paramref name="seconds"/> as of
        /// <paramref name="nowMs"/>.</summary>
        public readonly bool HasLasted(int seconds, long nowMs) =>
            Running && nowMs - StartedMs >= seconds * 1000L;

        public void End() => Running = false;
    }

    /// <summary>How long the machine has been hot with the fans not keeping up.</summary>
    private Run _hot;

    /// <summary>How long it has been reading below the re-arm temperature.</summary>
    private Run _cool;

    /// <summary>When the last poll arrived, and the cadence it claimed to be arriving at. Together
    /// they are what says whether the next one continues a run or interrupts it.</summary>
    private long _lastPollMs;
    private int _lastExpectedIntervalMs;
    private bool _hasPolled;

    /// <summary>
    /// Feeds one poll in and answers what, if anything, should be written to the controller right
    /// now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both runs are functions of the readings, the clock and nothing else: a poll that is not hot
    /// ends the hot run, a poll that is not cool ends the cool run, and nothing outside this
    /// method ever touches either. That is what makes "five seconds" and "fifteen seconds" mean
    /// what they say, and it is why a machine crossing back and forth over the danger zone gets
    /// neither answer rather than both.
    /// </para>
    /// <para>
    /// A run also has to have been <em>watched</em>, not merely spanned. If this poll arrives more
    /// than <see cref="FanSafety.WatchdogMaxPollGapFactor"/> times the expected interval after the
    /// last one - the app was suspended, the UI thread was starved, a tick was dropped - then
    /// whatever happened in between was not observed, and a run cannot be closed on the strength of
    /// two samples with a silence between them. Both runs end and restart from this poll. The
    /// allowance is measured against the wider of the two cadences either poll claimed, so the
    /// window being opened or hidden mid-run - which changes the rate by a factor of five in one
    /// step - is an ordinary poll and not a gap. A clock that moved backwards is treated the same
    /// way: not a measurement, so not a run.
    /// </para>
    /// <para>
    /// <see cref="WatchdogAction.Force"/> is returned on a transition, never for as long as the
    /// dangerous state lasts: applying a mode is a five- to seven-step write sequence paced at
    /// 500 ms a step, and the duty read-back lags it, so a condition-based answer would queue a
    /// fresh sequence every second for the whole time the machine stayed hot. There are exactly
    /// two transitions available to one hot machine - into <see cref="WatchdogStage.Raised"/> and
    /// then into <see cref="WatchdogStage.Maximum"/> - and the second one is only offered once the
    /// first one's write has been accounted for by <see cref="NoteForcedMode"/>. Polls arriving
    /// during that write read a duty that predates it and must not be allowed to escalate on it.
    /// The sustained run gates the <em>start</em> of an emergency and not the escalation: by then
    /// the machine has already spent its five seconds proving itself and has had a whole write
    /// sequence land on it without the duty moving.
    /// </para>
    /// <para>
    /// <see cref="WatchdogAction.Release"/> is a transition too, and it is offered only from a
    /// stage. Producing it sets <see cref="Stage"/> back to <see cref="WatchdogStage.None"/>
    /// first, so the write it asks for - the owner's own mode, which re-arms this watchdog through
    /// <see cref="NoteModeApplied"/> like any other apply - lands on a watchdog that is already
    /// re-armed. The re-arm is therefore a no-op rather than the start of a fresh cycle, and since
    /// a release cannot be produced from <see cref="WatchdogStage.None"/> it cannot produce
    /// another release. Getting back to a stage from there costs
    /// <see cref="FanSafety.WatchdogSecondsToFire"/> unbroken seconds at
    /// <see cref="FanSafety.WatchdogTriggerTemperature"/> °C, every reading of which ends the cool
    /// run - which a machine that has just spent fifteen seconds below
    /// <see cref="FanSafety.WatchdogRearmTemperature"/> °C is not doing.
    /// </para>
    /// <para>
    /// A reading that could not have come from a running laptop - the 0 a failed WMI read
    /// surfaces as, or the 255 a wedged controller reports - is not acted on and, just as
    /// importantly, is not counted as the machine having cooled down. It breaks the cool run
    /// rather than extending it, because handing the fans back on the strength of a number that
    /// is not a temperature is exactly the mistake this is here to avoid.
    /// </para>
    /// </remarks>
    /// <param name="cpuCelsius">The CPU temperature from this poll, in °C. Raw, as read - a
    /// smoothed value would hide the spikes this is watching for.</param>
    /// <param name="dutyPercent">The CPU fan's duty from this poll, in percent.</param>
    /// <param name="nowMs">When this poll happened, as a monotonic millisecond timestamp; the app
    /// passes <see cref="Environment.TickCount64"/>. Passed in rather than read here so this stays
    /// a pure function of its inputs and its runs can be driven without anything sleeping.</param>
    /// <param name="expectedPollIntervalMs">How often the caller is polling, in milliseconds -
    /// <see cref="SensorPoller.IntervalMs"/>, which moves when the window is shown or hidden. Used
    /// only to tell an ordinary poll from a late one; the runs themselves are pure elapsed
    /// time.</param>
    /// <returns>What this poll asks for.</returns>
    public WatchdogAction Observe(int cpuCelsius, int dutyPercent, long nowMs, int expectedPollIntervalMs)
    {
        if (!ContinuesTheRun(nowMs, expectedPollIntervalMs))
        {
            _hot.End();
            _cool.End();
        }

        _lastPollMs = nowMs;
        _lastExpectedIntervalMs = expectedPollIntervalMs;
        _hasPolled = true;

        if (!FanSafety.IsPlausibleTemperature(cpuCelsius))
        {
            _hot.End();
            _cool.End();
            return WatchdogAction.None;
        }

        _hot.Observe(IsDangerous(cpuCelsius, dutyPercent), nowMs);
        _cool.Observe(cpuCelsius < FanSafety.WatchdogRearmTemperature, nowMs);

        if (Stage == WatchdogStage.None)
        {
            if (!_hot.HasLasted(FanSafety.WatchdogSecondsToFire, nowMs)) return WatchdogAction.None;
            Stage = WatchdogStage.Raised;
            _forcing = true;
            return WatchdogAction.Force;
        }

        // The sequence this watchdog asked for is still going out, so this poll is older than it.
        // Escalating on it would pin the fans at maximum on the strength of a duty read before the
        // gentler answer had been written at all, and handing them back on it would race the write
        // that is still in flight. Nothing is lost by waiting: the runs keep running.
        if (_forcing) return WatchdogAction.None;

        if (_cool.HasLasted(FanSafety.WatchdogSecondsToRelease, nowMs))
        {
            Stage = WatchdogStage.None;
            return WatchdogAction.Release;
        }

        // Nothing lives above the last resort, so there is no third sequence to send.
        if (Stage != WatchdogStage.Raised) return WatchdogAction.None;

        // This poll, not a run of them: the machine has already proved itself once and then had
        // the gentler answer written to it without the duty moving.
        if (!_hot.Running) return WatchdogAction.None;
        Stage = WatchdogStage.Maximum;
        _forcing = true;
        return WatchdogAction.Force;
    }

    /// <summary>
    /// Whether a poll arriving at <paramref name="nowMs"/> is close enough behind the last one to
    /// be extending the runs it left rather than interrupting them.
    /// </summary>
    /// <remarks>
    /// Both halves of the answer are about a run being <em>watched</em> and not merely spanned.
    ///
    /// A gap wider than <see cref="FanSafety.WatchdogMaxPollGapFactor"/> intervals is a stretch of
    /// time nothing looked at - a suspended machine, a starved UI thread, a dropped tick - and the
    /// reading that ends it says only what is true now. Closing a five-second run on it would
    /// force the fans on one sample; closing a fifteen-second one would hand them back on one.
    ///
    /// The allowance uses the wider of the two cadences because the rate legitimately changes
    /// mid-run, by a factor of five, whenever the window is shown or hidden. The poll straight
    /// after that change is late by one of the two and early by the other, and must not be read as
    /// a gap either way round - a run that survives being watched more closely is still a run.
    ///
    /// A backwards jump - <paramref name="nowMs"/> before the last poll, or an elapsed time that
    /// came out negative through overflow - is not a measurement at all. It errs the same way:
    /// break the run and start again, which costs at worst one more run of the same length.
    ///
    /// The very first poll continues nothing, which is why it answers false. There are no runs
    /// yet, so there is nothing for that to end.
    /// </remarks>
    private bool ContinuesTheRun(long nowMs, int expectedPollIntervalMs)
    {
        if (!_hasPolled) return false;

        var elapsedMs = nowMs - _lastPollMs;
        if (nowMs < _lastPollMs || elapsedMs < 0) return false;

        // No cadence below the poller's own floor is real, so a caller reporting one cannot make
        // every ordinary poll look like a gap and quietly switch the guard off.
        var intervalMs = Math.Max(
            SensorPoller.MinIntervalMs, Math.Max(_lastExpectedIntervalMs, expectedPollIntervalMs));
        return elapsedMs <= (long)FanSafety.WatchdogMaxPollGapFactor * intervalMs;
    }

    /// <summary>Whether this poll shows a machine that is too hot with fans doing too little
    /// about it. The same question at both stages - only the answer to it changes.</summary>
    private static bool IsDangerous(int cpuCelsius, int dutyPercent) =>
        cpuCelsius >= FanSafety.WatchdogTriggerTemperature && dutyPercent < FanSafety.WatchdogDutyFloor;

    /// <summary>
    /// Reports what became of the write this watchdog asked for.
    /// </summary>
    /// <remarks>
    /// The latch is set the moment <see cref="Observe"/> asks for a mode, because the write takes
    /// a few seconds and the polls arriving during it must not start a second one or read its
    /// stage as settled. It is only that in-flight guard until this is called: a write that never
    /// reached the controller leaves the machine both too hot and with no more cooling than it
    /// started with, which is the last state in which to stop trying, so a failure re-arms and the
    /// next poll attempts it again.
    ///
    /// A failure re-arms all the way to <see cref="WatchdogStage.None"/> rather than holding the
    /// stage it failed at, because the retry has to be the stage that never landed. Escalating
    /// past a first stage the controller never received would pin the fans at maximum without
    /// ever having tried the mode that could have avoided it. The hot run is deliberately left
    /// alone, so the retry is the next poll and not five seconds later: the machine has already
    /// proved itself sustained, and this is not the moment to make it prove it again.
    /// </remarks>
    /// <param name="applied">True if the write reached the controller.</param>
    public void NoteForcedMode(bool applied)
    {
        _forcing = false;
        if (!applied) Stage = WatchdogStage.None;
    }

    /// <summary>
    /// Reports that a fan mode has been applied to the controller.
    /// </summary>
    /// <remarks>
    /// Re-arms, because whatever was just written has replaced what the watchdog forced and with
    /// it the only reason the latch exists. A resume is what makes this matter: the saved mode
    /// goes back on while the CPU is still above <see cref="FanSafety.WatchdogRearmTemperature"/>
    /// °C, and without this the machine would sit hot on a slow mode with the guard latched shut.
    ///
    /// The watchdog's own forced writes are the ones this ignores. Both stages raise
    /// <see cref="FanController.Applied"/> from inside the sequence, before
    /// <see cref="NoteForcedMode"/> can be reached, so <c>_forcing</c> is still set when this
    /// arrives for them - and letting the first stage re-arm on its own apply would throw away
    /// the stage it just reached and send that same sequence again on the next poll, which is the
    /// per-second re-drive the latch exists to prevent.
    ///
    /// The release write is the third apply that reaches here, and it needs no guard of its own:
    /// <see cref="Observe"/> has already set the stage back to <see cref="WatchdogStage.None"/>
    /// before asking for it, so this finds nothing left to re-arm and changes nothing.
    /// </remarks>
    public void NoteModeApplied()
    {
        if (_forcing) return;
        Stage = WatchdogStage.None;
    }
}
