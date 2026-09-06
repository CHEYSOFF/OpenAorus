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
/// </remarks>
public sealed class FanWatchdog
{
    /// <summary>Whether the watchdog has fired and is waiting for the machine to cool before it
    /// could fire again.</summary>
    public bool HasFired { get; private set; }

    /// <summary>
    /// Feeds one poll in and answers whether Turbo should be applied right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True is returned on the transition into the dangerous state, never for as long as it
    /// lasts: applying Turbo is a seven-step write sequence paced at 500 ms a step, and the duty
    /// read-back lags it, so a condition-based answer would queue a fresh sequence every second
    /// for the whole time the machine stayed hot. Once fired it stays latched until the CPU comes
    /// back down below <see cref="FanSafety.WatchdogRearmTemperature"/> °C.
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
        return true;
    }
}
