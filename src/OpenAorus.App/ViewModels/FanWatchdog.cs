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
/// timer that ticks about once a second, so it is really a small state machine and it lives here
/// on its own rather than inside the poller's callback where nothing could reach it.
/// <see cref="MainViewModel"/> owns one and does the acting; this decides.
/// </para>
/// <para>
/// Both ends of an emergency are counted rather than instantaneous, and the two counts are
/// deliberately different sizes. <see cref="FanSafety.WatchdogPollsToFire"/> consecutive
/// qualifying polls - about five seconds - are needed before anything is forced, because this CPU
/// boosts into the 90s constantly and the controller's own default table already asks for maximum
/// fans up there: a single reading over the trigger is not evidence of a misconfigured machine.
/// <see cref="FanSafety.WatchdogPollsToRelease"/> consecutive polls below
/// <see cref="FanSafety.WatchdogRearmTemperature"/> °C - about fifteen - are needed before the
/// fans are handed back. Three times as long to let go as to engage is what makes oscillation
/// impossible: a machine flickering across the danger zone can satisfy neither run.
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

    /// <summary>How many polls in a row have been hot with the fans not keeping up, capped at the
    /// count that matters.</summary>
    private int _dangerousPolls;

    /// <summary>How many polls in a row have read below the re-arm temperature, capped likewise.</summary>
    private int _coolPolls;

    /// <summary>
    /// Feeds one poll in and answers what, if anything, should be written to the controller right
    /// now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both counters are functions of the readings and of nothing else: a poll that is not hot
    /// zeroes the hot run, a poll that is not cool zeroes the cool run, and nothing outside this
    /// method ever touches either. That is what makes "five in a row" and "fifteen in a row" mean
    /// what they say, and it is why a machine crossing back and forth over the danger zone gets
    /// neither answer rather than both.
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
    /// The sustained count gates the <em>start</em> of an emergency and not the escalation: by
    /// then the machine has already spent five polls proving itself and has had a whole write
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
    /// <see cref="FanSafety.WatchdogPollsToFire"/> consecutive polls at
    /// <see cref="FanSafety.WatchdogTriggerTemperature"/> °C, each of which zeroes the cool run -
    /// which a machine that has just spent fifteen polls below
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
    /// smoothed value would hide the spikes this is counting.</param>
    /// <param name="dutyPercent">The CPU fan's duty from this poll, in percent.</param>
    /// <returns>What this poll asks for.</returns>
    public WatchdogAction Observe(int cpuCelsius, int dutyPercent)
    {
        if (!FanSafety.IsPlausibleTemperature(cpuCelsius))
        {
            _dangerousPolls = 0;
            _coolPolls = 0;
            return WatchdogAction.None;
        }

        _dangerousPolls = IsDangerous(cpuCelsius, dutyPercent)
            ? Math.Min(_dangerousPolls + 1, FanSafety.WatchdogPollsToFire)
            : 0;
        _coolPolls = cpuCelsius < FanSafety.WatchdogRearmTemperature
            ? Math.Min(_coolPolls + 1, FanSafety.WatchdogPollsToRelease)
            : 0;

        if (Stage == WatchdogStage.None)
        {
            if (_dangerousPolls < FanSafety.WatchdogPollsToFire) return WatchdogAction.None;
            Stage = WatchdogStage.Raised;
            _forcing = true;
            return WatchdogAction.Force;
        }

        // The sequence this watchdog asked for is still going out, so this poll is older than it.
        // Escalating on it would pin the fans at maximum on the strength of a duty read before the
        // gentler answer had been written at all, and handing them back on it would race the write
        // that is still in flight. Nothing is lost by waiting: the runs keep counting.
        if (_forcing) return WatchdogAction.None;

        if (_coolPolls >= FanSafety.WatchdogPollsToRelease)
        {
            Stage = WatchdogStage.None;
            return WatchdogAction.Release;
        }

        // Nothing lives above the last resort, so there is no third sequence to send.
        if (Stage != WatchdogStage.Raised) return WatchdogAction.None;

        if (_dangerousPolls == 0) return WatchdogAction.None;
        Stage = WatchdogStage.Maximum;
        _forcing = true;
        return WatchdogAction.Force;
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
