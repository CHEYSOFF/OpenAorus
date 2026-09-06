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

    /// <summary>CPU temperature at which the running app forces Turbo, in °C.</summary>
    public const int WatchdogTriggerTemperature = 90;

    /// <summary>The duty below which the watchdog considers the fans to be doing too little, in percent.</summary>
    public const int WatchdogDutyFloor = 80;

    /// <summary>CPU temperature the machine must fall back below before the watchdog can fire again, in °C.</summary>
    public const int WatchdogRearmTemperature = 80;

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
