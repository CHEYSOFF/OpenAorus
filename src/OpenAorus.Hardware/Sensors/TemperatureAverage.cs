using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Sensors;

/// <summary>
/// A short rolling mean of a temperature, for the number a person reads off the screen.
/// </summary>
/// <remarks>
/// <para>
/// The poll runs about once a second and CPU temperature genuinely moves that fast - a boost of a
/// few hundred milliseconds can be twenty degrees - so a field redrawn from the raw sample every
/// tick strobes rather than reads. Averaging a few seconds of history gives the owner a number
/// they can actually take in, at the cost of a second or two of lag they will never notice.
/// </para>
/// <para>
/// This is cosmetic and must stay cosmetic. Nothing that decides anything may be fed from here:
/// the thermal watchdog counts consecutive raw samples on purpose, and handing it a mean would
/// blunt exactly the excursions it exists to notice while looking like it still worked. The
/// diagnostics dump and any hardware session read their values straight from
/// <see cref="SensorReader"/> for the same reason - what is written down about a machine has to be
/// what the machine said.
/// </para>
/// <para>
/// A reading that could not have come from a running laptop is dropped rather than averaged in.
/// A failed WMI read surfaces as 0 and a wedged controller reports 255; folding either into the
/// mean would produce the very lurch this removes, from a sensor failure the banner is already
/// reporting in its own words.
/// </para>
/// </remarks>
public sealed class TemperatureAverage
{
    /// <summary>The window the window uses, in samples.</summary>
    /// <remarks>At roughly a poll a second this is a few seconds of history: long enough for the
    /// number to settle, short enough that it still follows the machine. Deliberately not tied to
    /// any of the watchdog's counts - the two are unrelated jobs that both happen to count polls,
    /// and sharing a constant would let a change made for legibility move when the fans are taken
    /// off the owner.</remarks>
    public const int DisplaySamples = 6;

    private readonly int[] _window;
    private int _count;
    private int _next;
    private int _sum;

    /// <summary>The mean of the samples currently in the window, in °C, rounded to a whole
    /// degree. 0 until the first plausible reading arrives.</summary>
    public int Value { get; private set; }

    /// <summary>Builds an average over the last <paramref name="samples"/> plausible readings.</summary>
    /// <param name="samples">How many readings the window holds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="samples"/> is less than 1.</exception>
    public TemperatureAverage(int samples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);
        _window = new int[samples];
    }

    /// <summary>Adds one reading and returns the number to show.</summary>
    /// <param name="celsius">The reading, in °C, exactly as the sensor gave it.</param>
    /// <returns><see cref="Value"/> after this reading - unchanged if the reading was not one.</returns>
    public int Add(int celsius)
    {
        if (!FanSafety.IsPlausibleTemperature(celsius)) return Value;

        if (_count == _window.Length) _sum -= _window[_next];
        else _count++;

        _window[_next] = celsius;
        _sum += celsius;
        _next = (_next + 1) % _window.Length;

        Value = (int)Math.Round((double)_sum / _count, MidpointRounding.AwayFromZero);
        return Value;
    }
}
