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

/// <summary>
/// Decides, one sensor poll at a time, whether the machine needs its fans taken off the owner -
/// and, if so, how hard.
/// </summary>
/// <remarks>
/// <para>
/// The rule itself is one line - hot CPU, fans not keeping up, override them - but it is fed by a
/// timer that ticks about once a second, so it is really a small state machine and it lives here
/// on its own rather than inside the poller's callback where nothing could reach it.
/// <see cref="MainViewModel"/> owns one and does the acting; this decides.
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
    /// <remarks>Read by <see cref="MainViewModel"/> on the poll <see cref="Observe"/> returns true
    /// for. Which mode belongs to which stage is <see cref="FanSafety"/>'s to say, so the two
    /// names live there beside the temperatures and the duty floor rather than being spelt out at
    /// the call site.</remarks>
    public FanMode? ModeToApply => Stage switch
    {
        WatchdogStage.Raised => FanSafety.WatchdogFirstStageMode,
        WatchdogStage.Maximum => FanSafety.WatchdogLastResortMode,
        _ => null,
    };

    /// <summary>True between the poll that asked for a mode and the outcome of the write it asked
    /// for. That write is itself an applied mode, and it is the one thing that must not re-arm.</summary>
    private bool _forcing;

    /// <summary>
    /// Feeds one poll in and answers whether the stage this watchdog has just moved to should be
    /// applied right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True is returned on a transition, never for as long as the dangerous state lasts: applying
    /// a mode is a five- to seven-step write sequence paced at 500 ms a step, and the duty
    /// read-back lags it, so a condition-based answer would queue a fresh sequence every second
    /// for the whole time the machine stayed hot. There are exactly two transitions available to
    /// one hot machine - into <see cref="WatchdogStage.Raised"/> and then into
    /// <see cref="WatchdogStage.Maximum"/> - and the second one is only offered once the first
    /// one's write has been accounted for by <see cref="NoteForcedMode"/>. Polls arriving during
    /// that write read a duty that predates it and must not be allowed to escalate on it.
    /// </para>
    /// <para>
    /// Past the last resort the latch behaves exactly as it did when there was only one stage: it
    /// holds until the CPU comes back down below <see cref="FanSafety.WatchdogRearmTemperature"/>
    /// °C, or until <see cref="NoteForcedMode"/> or <see cref="NoteModeApplied"/> says what was
    /// forced is no longer what the fans are running. Re-arming always goes back to
    /// <see cref="WatchdogStage.None"/>: the next emergency is a new one and starts at the stage
    /// that still tracks temperature.
    /// </para>
    /// <para>
    /// A reading that could not have come from a running laptop - the 0 a failed WMI read
    /// surfaces as, or the 255 a wedged controller reports - is not acted on and, just as
    /// importantly, is not counted as the machine having cooled down. Re-arming on a broken
    /// sensor would mean the next real reading over the trigger starting the escalation again.
    /// </para>
    /// </remarks>
    /// <param name="cpuCelsius">The CPU temperature from this poll, in °C.</param>
    /// <param name="dutyPercent">The CPU fan's duty from this poll, in percent.</param>
    /// <returns>True on the poll that should apply <see cref="ModeToApply"/>.</returns>
    public bool Observe(int cpuCelsius, int dutyPercent)
    {
        if (!FanSafety.IsPlausibleTemperature(cpuCelsius)) return false;

        if (Stage == WatchdogStage.None)
        {
            if (!IsDangerous(cpuCelsius, dutyPercent)) return false;
            Stage = WatchdogStage.Raised;
            _forcing = true;
            return true;
        }

        if (cpuCelsius < FanSafety.WatchdogRearmTemperature)
        {
            Stage = WatchdogStage.None;
            return false;
        }

        // The sequence this watchdog asked for is still going out, so the duty on this poll is
        // older than it. Escalating here would pin the fans at maximum on the strength of a
        // reading taken before the gentler answer had been written at all.
        if (_forcing) return false;

        // Nothing lives above the last resort, so there is no third sequence to send.
        if (Stage != WatchdogStage.Raised) return false;

        if (!IsDangerous(cpuCelsius, dutyPercent)) return false;
        Stage = WatchdogStage.Maximum;
        _forcing = true;
        return true;
    }

    /// <summary>Whether this poll shows a machine that is too hot with fans doing too little
    /// about it. The same question at both stages - only the answer to it changes.</summary>
    private static bool IsDangerous(int cpuCelsius, int dutyPercent) =>
        cpuCelsius >= FanSafety.WatchdogTriggerTemperature && dutyPercent < FanSafety.WatchdogDutyFloor;

    /// <summary>
    /// Reports what became of the write this watchdog asked for.
    /// </summary>
    /// <remarks>
    /// The latch is set the moment <see cref="Observe"/> fires, because the write takes a few
    /// seconds and the polls arriving during it must not start a second one or read its stage as
    /// settled. It is only that in-flight guard until this is called: a write that never reached
    /// the controller leaves the machine both too hot and with no more cooling than it started
    /// with, which is the last state in which to stop trying, so a failure re-arms and the next
    /// poll attempts it again.
    ///
    /// A failure re-arms all the way to <see cref="WatchdogStage.None"/> rather than holding the
    /// stage it failed at, because the retry has to be the stage that never landed. Escalating
    /// past a first stage the controller never received would pin the fans at maximum without
    /// ever having tried the mode that could have avoided it.
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
    /// The watchdog's own writes are the ones this ignores. Both stages raise
    /// <see cref="FanController.Applied"/> from inside the sequence, before
    /// <see cref="NoteForcedMode"/> can be reached, so <c>_forcing</c> is still set when this
    /// arrives for them - and letting the first stage re-arm on its own apply would throw away
    /// the stage it just reached and send that same sequence again on the next poll, which is the
    /// per-second re-drive the latch exists to prevent.
    /// </remarks>
    public void NoteModeApplied()
    {
        if (_forcing) return;
        Stage = WatchdogStage.None;
    }
}
