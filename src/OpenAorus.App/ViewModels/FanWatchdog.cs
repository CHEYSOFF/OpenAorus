using OpenAorus.Hardware.Fans;

namespace OpenAorus.App.ViewModels;

/// <summary>
/// Decides, one sensor poll at a time, whether the machine needs Turbo forced on it.
/// </summary>
/// <remarks>
/// <para>
/// The rule itself is one line - hot CPU, fans not keeping up, force Turbo - but it is fed by a
/// timer that ticks about once a second, so it is really a small state machine and it lives here
/// on its own rather than inside the poller's callback where nothing could reach it.
/// <see cref="MainViewModel"/> owns one and does the acting; this decides.
/// </para>
/// <para>
/// It is deliberately mode-agnostic. Gaming and Turbo already run the fans above
/// <see cref="FanSafety.WatchdogDutyFloor"/>, so the duty half of the condition is what keeps it
/// from firing there - no mode needs naming, and nothing has to be kept in step when one is added.
/// </para>
/// <para>
/// The latch only ever exists to stop a second Turbo being queued on top of one that is still in
/// force, so it is fed by the two things that end that: <see cref="NoteForcedTurbo"/>, which says
/// whether the Turbo was ever written at all, and <see cref="NoteModeApplied"/>, which says
/// something else has since overridden it.
/// </para>
/// </remarks>
public sealed class FanWatchdog
{
    /// <summary>Whether the watchdog has fired and is waiting for the machine to cool before it
    /// could fire again.</summary>
    public bool HasFired { get; private set; }

    /// <summary>True between the poll that fired and the outcome of the Turbo write it asked for.
    /// That write is itself an applied mode, and it is the one thing that must not re-arm.</summary>
    private bool _forcing;

    /// <summary>
    /// Feeds one poll in and answers whether Turbo should be applied right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True is returned on the transition into the dangerous state, never for as long as it
    /// lasts: applying Turbo is a seven-step write sequence paced at 500 ms a step, and the duty
    /// read-back lags it, so a condition-based answer would queue a fresh sequence every second
    /// for the whole time the machine stayed hot. Once fired it stays latched until the CPU comes
    /// back down below <see cref="FanSafety.WatchdogRearmTemperature"/> °C - or until one of
    /// <see cref="NoteForcedTurbo"/> and <see cref="NoteModeApplied"/> says the Turbo it forced is
    /// no longer what the fans are running.
    /// </para>
    /// <para>
    /// A reading that could not have come from a running laptop - the 0 a failed WMI read
    /// surfaces as, or the 255 a wedged controller reports - is not acted on and, just as
    /// importantly, is not counted as the machine having cooled down. Re-arming on a broken
    /// sensor would mean the next real reading over the trigger fires a second time.
    /// </para>
    /// </remarks>
    /// <param name="cpuCelsius">The CPU temperature from this poll, in °C.</param>
    /// <param name="dutyPercent">The CPU fan's duty from this poll, in percent.</param>
    /// <returns>True on the poll that should force Turbo.</returns>
    public bool Observe(int cpuCelsius, int dutyPercent)
    {
        if (!FanSafety.IsPlausibleTemperature(cpuCelsius)) return false;

        if (HasFired)
        {
            if (cpuCelsius < FanSafety.WatchdogRearmTemperature) HasFired = false;
            return false;
        }

        if (cpuCelsius < FanSafety.WatchdogTriggerTemperature) return false;
        if (dutyPercent >= FanSafety.WatchdogDutyFloor) return false;

        HasFired = true;
        _forcing = true;
        return true;
    }

    /// <summary>
    /// Reports what became of the Turbo write this watchdog asked for.
    /// </summary>
    /// <remarks>
    /// The latch is set the moment <see cref="Observe"/> fires, because the write takes a few
    /// seconds and the polls arriving during it must not start a second one. It is only that
    /// in-flight guard until this is called: a write that never reached the controller leaves the
    /// machine both too hot and with no more cooling than it started with, which is the last state in
    /// which to stop trying, so a failure re-arms and the next poll attempts it again.
    /// </remarks>
    /// <param name="applied">True if the write reached the controller.</param>
    public void NoteForcedTurbo(bool applied)
    {
        _forcing = false;
        if (!applied) HasFired = false;
    }

    /// <summary>
    /// Reports that a fan mode has been applied to the controller.
    /// </summary>
    /// <remarks>
    /// Re-arms, because whatever was just written has replaced the forced Turbo and with it the
    /// only reason the latch exists. A resume is what makes this matter: the saved mode goes back
    /// on while the CPU is still above <see cref="FanSafety.WatchdogRearmTemperature"/> °C, and
    /// without this the machine would sit hot on a slow mode with the guard latched shut.
    ///
    /// The watchdog's own Turbo is the one apply this ignores - re-arming on it would put the
    /// per-second re-drive the latch exists to prevent straight back.
    /// </remarks>
    public void NoteModeApplied()
    {
        if (_forcing) return;
        HasFired = false;
    }
}
