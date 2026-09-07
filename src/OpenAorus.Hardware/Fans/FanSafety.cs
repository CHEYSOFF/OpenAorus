namespace OpenAorus.Hardware.Fans;

/// <summary>
/// The floors that stop a fan setting from leaving the machine with no cooling.
/// </summary>
/// <remarks>
/// <para>
/// Both dangerous settings survive the app: Fixed latches <c>SetFixedFanStatus</c> in the
/// embedded controller and a custom curve is written into the controller's own table, so a
/// setting made once governs the fans after the app is closed and after a reboot. Nothing in
/// the recovered protocol documents a controller-side minimum or an emergency override, so the
/// only place these limits can exist is here.
/// </para>
/// <para>
/// This is the single definition. <see cref="FanCurve.Validate"/>, <see cref="FanController"/>,
/// the settings loader and the Fixed slider all read the numbers from here rather than carrying
/// their own copy, so there is no way to tighten one and leave another behind.
/// </para>
/// <para>
/// A curve that asks for 0 % while the machine is cool is deliberately still legal: the
/// controller ramps it as the temperature rises, and that is how silent idle works. What is
/// rejected is a curve that never ramps.
/// </para>
/// </remarks>
public static class FanSafety
{
    /// <summary>The slowest Fixed duty the fans may be pinned to, in percent.</summary>
    /// <remarks>Fixed ignores temperature entirely, so this is the duty the machine is stuck with
    /// under any load until the owner changes it.</remarks>
    public const int MinFixedPercent = 20;

    /// <summary>The temperature at which a curve must already be pulling real air, in °C.</summary>
    public const int HotTemperature = 80;

    /// <summary>The least duty a curve may run at <see cref="HotTemperature"/>, in percent.</summary>
    public const int MinDutyWhenHot = 60;

    /// <summary>The temperature at which a curve must be at nearly full duty, in °C.</summary>
    public const int CriticalTemperature = 90;

    /// <summary>The least duty a curve may run at <see cref="CriticalTemperature"/>, in percent.</summary>
    public const int MinDutyWhenCritical = 90;

    /// <summary>The lowest temperature the last curve point may sit at, in °C.</summary>
    /// <remarks>Above its last point the controller holds that point's duty indefinitely, so a
    /// table that stops early hands the danger zone back to whatever duty it was last told.</remarks>
    public const int MinTopTemperature = 85;

    /// <summary>CPU temperature at which the running app takes the fans off the owner, in °C.</summary>
    /// <remarks>
    /// Above <see cref="CriticalTemperature"/> on purpose, and not the same kind of number. The
    /// curve rules above describe where a table must already be working; this describes where the
    /// machine is being cooked. The AORUS 17G KD's own default table tops out at 89 °C asking for
    /// duty 229 - the controller's maximum - so the firmware treats the high 80s as the ordinary
    /// temperature for full fans, and this CPU boosts into the 90s under any real load. A trigger
    /// sitting inside that range fires on a normal Tuesday. At 95 °C with the measured duty still
    /// under <see cref="WatchdogDutyFloor"/> %, the controller is not doing what its own table
    /// says it should, which is the genuinely misconfigured machine this exists for.
    /// </remarks>
    public const int WatchdogTriggerTemperature = 95;

    /// <summary>The duty below which the watchdog considers the fans to be doing too little, in percent.</summary>
    public const int WatchdogDutyFloor = 80;

    /// <summary>How long the machine must hold the qualifying reading before the watchdog forces
    /// anything, in seconds.</summary>
    /// <remarks>
    /// A duration and not a count of polls, because the poll rate is not fixed. The window being
    /// open or in the tray moves it between
    /// <see cref="Config.AppSettings.PollIntervalVisibleMs"/> and
    /// <see cref="Config.AppSettings.PollIntervalHiddenMs"/>, and both are settings the owner can
    /// change, so a count of five polls meant five seconds with the window open and twenty-five
    /// with it in the tray - the slowest response on the machine nobody is watching, which for a
    /// background utility is the ordinary case. This is five seconds of a machine that is
    /// genuinely hot with the fans genuinely not keeping up, at whatever rate the app happens to
    /// be polling. A momentary spike - which on this CPU is most of them - must cost nothing at
    /// all.
    /// </remarks>
    public const int WatchdogSecondsToFire = 5;

    /// <summary>How long the CPU must hold below <see cref="WatchdogRearmTemperature"/> before the
    /// watchdog hands the fans back to the owner, in seconds.</summary>
    /// <remarks>Three times <see cref="WatchdogSecondsToFire"/>, and the asymmetry is the point:
    /// engaging is cheap and quick, letting go is slow and has to be earned. A machine crossing
    /// back and forth over the danger zone can complete neither run, so the two can never chase
    /// each other. Being a duration, it is the same fifteen seconds with the window open and with
    /// it in the tray, which is what the owner waiting seventy-five for their fans was not
    /// getting.</remarks>
    public const int WatchdogSecondsToRelease = 15;

    /// <summary>How many times the expected poll interval one poll may be late before the run it
    /// would have extended counts as broken.</summary>
    /// <remarks>
    /// A duration alone is not the whole of "sustained": two readings an hour apart span far more
    /// than <see cref="WatchdogSecondsToFire"/> seconds while saying nothing about the hour
    /// between them. A suspended machine, a starved UI thread or a simply skipped poll all produce
    /// that shape, and letting one late reading close a run would force the fans - or hand them
    /// back - on the strength of a single sample. So a run has to be observed across its whole
    /// window, and a gap this much wider than the cadence the app is actually polling at means it
    /// was not. Wide enough that a late tick on a busy machine costs nothing; narrow enough that a
    /// sleep cannot be mistaken for evidence.
    /// </remarks>
    public const int WatchdogMaxPollGapFactor = 3;

    /// <summary>CPU temperature the machine must fall back below before the watchdog hands the
    /// fans back and can fire again, in °C.</summary>
    public const int WatchdogRearmTemperature = 80;

    /// <summary>The mode the watchdog puts the fans on the first time it fires.</summary>
    /// <remarks>
    /// The controller's own aggressive automatic curve. It is the first answer rather than the
    /// only one because it keeps reading the temperature: it lifts the fans while the machine is
    /// hot and lets them back down as it cools, without anything here having to decide when.
    /// Nothing assumes what that curve actually does at <see cref="WatchdogTriggerTemperature"/> °C
    /// - it is an unverified part of the recovered protocol - which is why a second stage exists
    /// and why the measured duty, not a guess about the curve, is what reaches for it.
    /// </remarks>
    public const FanMode WatchdogFirstStageMode = FanMode.Gaming;

    /// <summary>The mode the watchdog escalates to when the first stage did not lift the duty.</summary>
    /// <remarks>
    /// Turbo pins both fans at the profile's DutyMax and, being a fixed-duty mode, stops
    /// responding to temperature entirely - it runs at full until something else changes the mode,
    /// including long after the machine is cold. That cost is worth paying only once the measured
    /// duty has said the gentler answer did not work, so this is the last resort and nothing
    /// escalates past it.
    /// </remarks>
    public const FanMode WatchdogLastResortMode = FanMode.Turbo;

    /// <summary>Coldest temperature reading treated as real, in °C. A failed read surfaces as 0.</summary>
    public const int MinPlausibleTemperature = 1;

    /// <summary>Hottest temperature reading treated as real, in °C. A failed read can also surface as 255.</summary>
    public const int MaxPlausibleTemperature = 120;

    /// <summary>
    /// The duty the controller runs at <paramref name="temperature"/> given <paramref name="points"/>.
    /// </summary>
    /// <remarks>
    /// The table is a step function, not an interpolation: the duty is the one named by the
    /// highest point whose temperature is at or below <paramref name="temperature"/>. Below every
    /// point the first point's duty applies, and above the last point that last duty is held.
    /// </remarks>
    /// <param name="points">The curve's points. Empty reads as 0 %.</param>
    /// <param name="temperature">The temperature to read the table at, in °C.</param>
    /// <returns>The duty in percent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> is null.</exception>
    public static int DutyAt(IReadOnlyList<FanCurvePoint> points, int temperature)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) return 0;

        var bestTemperature = int.MinValue;
        var duty = points[0].DutyPercent;   // below every point, the first slot governs
        foreach (var p in points)
        {
            if (p.Temperature <= temperature && p.Temperature > bestTemperature)
            {
                bestTemperature = p.Temperature;
                duty = p.DutyPercent;
            }
        }
        return duty;
    }

    /// <summary>Whether a Fixed duty is at or above <see cref="MinFixedPercent"/>.</summary>
    /// <param name="percent">The duty in percent.</param>
    /// <returns>True if the duty may be applied as-is.</returns>
    public static bool IsFixedPercentSafe(int percent) => percent >= MinFixedPercent;

    /// <summary>Brings a Fixed duty inside <see cref="MinFixedPercent"/>..100.</summary>
    /// <remarks>Clamping rather than refusing is deliberate: refusing a Fixed apply leaves the
    /// controller latched wherever it already was, which on a machine that has been set to 0 %
    /// once is exactly the state being guarded against.</remarks>
    /// <param name="percent">The duty in percent.</param>
    /// <returns>The duty that may safely be written.</returns>
    public static int ClampFixedPercent(int percent) => Math.Clamp(percent, MinFixedPercent, 100);

    /// <summary>Whether a temperature reading could have come from a running laptop.</summary>
    /// <remarks>A failed WMI read surfaces as 0 and a wedged controller can report 255; neither
    /// is a temperature, and neither may be acted on or treated as the machine being cool.</remarks>
    /// <param name="celsius">The reading, in °C.</param>
    /// <returns>True if the reading may be used.</returns>
    public static bool IsPlausibleTemperature(int celsius) =>
        celsius >= MinPlausibleTemperature && celsius <= MaxPlausibleTemperature;

    /// <summary>
    /// The ways <paramref name="points"/> would leave the machine uncooled when hot, in the
    /// owner's terms.
    /// </summary>
    /// <remarks>Structural problems (too few points, temperatures out of order) are not checked
    /// here - <see cref="FanCurve.Validate"/> reports those first and then appends these.</remarks>
    /// <param name="points">The curve's points. Empty produces no errors; it is already rejected
    /// for having too few points.</param>
    /// <returns>One string per broken rule; empty when the curve is safe.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> is null.</exception>
    public static IReadOnlyList<string> CurveErrors(IReadOnlyList<FanCurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var errors = new List<string>();
        if (points.Count == 0) return errors;

        var hot = DutyAt(points, HotTemperature);
        if (hot < MinDutyWhenHot)
            errors.Add($"Curve needs at least {MinDutyWhenHot} % at {HotTemperature} °C; this one runs {hot} % there.");

        var critical = DutyAt(points, CriticalTemperature);
        if (critical < MinDutyWhenCritical)
            errors.Add($"Curve needs at least {MinDutyWhenCritical} % at {CriticalTemperature} °C; this one runs {critical} % there.");

        var top = points[^1].Temperature;
        if (top < MinTopTemperature)
            errors.Add($"The last point must be at {MinTopTemperature} °C or above so the curve still governs above it; this one ends at {top} °C.");

        return errors;
    }
}
