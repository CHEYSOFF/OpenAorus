using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Pins the one definition of "duty at a temperature" and the floors built on top of it.
/// </summary>
/// <remarks>
/// Every curve rule in <see cref="FanCurve.Validate"/> is expressed as a duty read off the table
/// at 80 °C and at 90 °C, so if the step lookup drifts the rules silently start guarding a
/// different curve than they read as guarding. That makes these the load-bearing tests of the
/// whole guard, not incidental coverage of a helper.
/// </remarks>
public class FanSafetyTests
{
    private static readonly FanCurvePoint[] Table =
    {
        new(40, 25), new(60, 40), new(80, 75), new(90, 100),
    };

    [Fact]
    public void Duty_below_every_point_is_the_first_points_duty()
    {
        // The EC holds the first slot's duty until the first temperature is reached, so a curve
        // that starts at 40 °C is running at its 40 °C duty at 20 °C, not at nothing.
        Assert.Equal(25, FanSafety.DutyAt(Table, 20));
        Assert.Equal(25, FanSafety.DutyAt(Table, 0));
    }

    [Fact]
    public void Duty_exactly_on_a_point_is_that_points_duty()
    {
        Assert.Equal(25, FanSafety.DutyAt(Table, 40));
        Assert.Equal(40, FanSafety.DutyAt(Table, 60));
        Assert.Equal(75, FanSafety.DutyAt(Table, 80));
        Assert.Equal(100, FanSafety.DutyAt(Table, 90));
    }

    [Fact]
    public void Duty_between_points_is_the_lower_points_duty()
    {
        // A step table, not an interpolation: 79 °C still runs the 60 °C slot's duty.
        Assert.Equal(40, FanSafety.DutyAt(Table, 79));
        Assert.Equal(75, FanSafety.DutyAt(Table, 89));
    }

    [Fact]
    public void Duty_above_the_last_point_is_the_last_points_duty()
    {
        // This is the reason the top point has to reach into the danger zone: above it the
        // controller keeps holding whatever duty the last slot named, forever.
        Assert.Equal(100, FanSafety.DutyAt(Table, 95));
        Assert.Equal(100, FanSafety.DutyAt(Table, 100));
    }

    [Fact]
    public void Duty_off_a_single_point_table_is_that_point_at_every_temperature()
    {
        var one = new[] { new FanCurvePoint(50, 30) };
        Assert.Equal(30, FanSafety.DutyAt(one, 10));
        Assert.Equal(30, FanSafety.DutyAt(one, 50));
        Assert.Equal(30, FanSafety.DutyAt(one, 100));
    }

    [Fact]
    public void Duty_off_an_empty_table_is_zero()
    {
        Assert.Equal(0, FanSafety.DutyAt(Array.Empty<FanCurvePoint>(), 90));
    }

    [Fact]
    public void The_shipped_default_curve_clears_every_curve_rule()
    {
        Assert.Empty(FanSafety.CurveErrors(FanCurve.Default.Points));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(100, true)]
    public void A_fixed_percent_is_safe_only_at_or_above_the_floor(int percent, bool safe)
    {
        Assert.Equal(safe, FanSafety.IsFixedPercentSafe(percent));
    }

    [Theory]
    [InlineData(0, FanSafety.MinFixedPercent)]
    [InlineData(-5, FanSafety.MinFixedPercent)]
    [InlineData(19, FanSafety.MinFixedPercent)]
    [InlineData(35, 35)]
    [InlineData(100, 100)]
    [InlineData(140, 100)]
    public void Clamping_a_fixed_percent_lifts_it_to_the_floor_and_caps_it_at_full(int percent, int expected)
    {
        Assert.Equal(expected, FanSafety.ClampFixedPercent(percent));
    }

    [Theory]
    [InlineData(0, false)]      // the value a failed getCpuTemp surfaces as
    [InlineData(-10, false)]
    [InlineData(255, false)]    // the EC's other failure value
    [InlineData(121, false)]
    [InlineData(1, true)]
    [InlineData(45, true)]
    [InlineData(95, true)]
    public void Only_a_temperature_a_running_laptop_could_report_is_plausible(int celsius, bool plausible)
    {
        Assert.Equal(plausible, FanSafety.IsPlausibleTemperature(celsius));
    }
}
