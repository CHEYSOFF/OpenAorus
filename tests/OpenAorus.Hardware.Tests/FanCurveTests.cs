using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Tests;

public class FanCurveTests
{
    [Fact]
    public void Default_curve_is_valid_and_ends_at_full_duty()
    {
        Assert.True(FanCurve.Default.IsValid);
        Assert.Equal(100, FanCurve.Default.Points[^1].DutyPercent);
        Assert.InRange(FanCurve.Default.Points.Count, 2, 15);
    }

    [Fact]
    public void Too_few_points_is_invalid()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(50, 40) });
        Assert.Contains(c.Validate(), e => e.Contains("at least 2"));
    }

    [Fact]
    public void More_than_15_points_is_invalid()
    {
        var pts = Enumerable.Range(0, 16).Select(i => new FanCurvePoint(20 + i * 5, 20 + i * 5));
        Assert.Contains(new FanCurve(pts).Validate(), e => e.Contains("at most 15"));
    }

    [Fact]
    public void Temperatures_must_strictly_increase()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(50, 30), new FanCurvePoint(50, 40) });
        Assert.Contains(c.Validate(), e => e.Contains("increasing"));
    }

    [Fact]
    public void Duty_must_not_decrease()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(40, 50), new FanCurvePoint(60, 40) });
        Assert.Contains(c.Validate(), e => e.Contains("decrease"));
    }

    [Fact]
    public void Values_outside_ranges_are_invalid()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(-1, 0), new FanCurvePoint(101, 120) });
        Assert.Contains(c.Validate(), e => e.Contains("0-100"));
    }

    [Fact]
    public void Valid_curve_has_no_errors()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(40, 30), new FanCurvePoint(70, 60), new FanCurvePoint(90, 100) });
        Assert.Empty(c.Validate());
        Assert.True(c.IsValid);
    }

    // ---- Safety rules ---------------------------------------------------------------
    // These carry the guard: the curve is written into the controller and governs the fans
    // after the app is closed, so a table that never ramps is a table that cooks the laptop
    // with nothing left running to notice.

    [Fact]
    public void A_curve_that_never_ramps_is_invalid()
    {
        var flat = new FanCurve(new[] { new FanCurvePoint(30, 0), new FanCurvePoint(90, 0) });
        Assert.False(flat.IsValid);
        Assert.Contains(flat.Validate(), e => e.Contains("60 %") && e.Contains("80 °C"));
    }

    [Fact]
    public void A_curve_too_slow_at_80_degrees_is_invalid()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(40, 20), new FanCurvePoint(80, 55), new FanCurvePoint(90, 100) });
        Assert.Contains(c.Validate(), e => e.Contains("60 %") && e.Contains("80 °C"));
    }

    [Fact]
    public void A_curve_too_slow_at_90_degrees_is_invalid()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(40, 20), new FanCurvePoint(80, 70), new FanCurvePoint(90, 85) });
        Assert.Contains(c.Validate(), e => e.Contains("90 %") && e.Contains("90 °C"));
    }

    [Fact]
    public void A_curve_that_stops_below_85_degrees_is_invalid()
    {
        // Above its last point the controller holds that point's duty for ever, so a table
        // ending at 80 °C leaves the danger zone entirely ungoverned.
        var c = new FanCurve(new[] { new FanCurvePoint(40, 30), new FanCurvePoint(80, 100) });
        Assert.Contains(c.Validate(), e => e.Contains("85 °C"));
    }

    [Fact]
    public void A_curve_that_ends_exactly_at_85_degrees_and_full_duty_is_valid()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(40, 60), new FanCurvePoint(85, 100) });
        Assert.Empty(c.Validate());
    }

    [Fact]
    public void A_curve_sitting_exactly_on_every_floor_is_valid()
    {
        var c = new FanCurve(new[]
        {
            new FanCurvePoint(40, 25), new FanCurvePoint(80, 60), new FanCurvePoint(90, 90),
        });
        Assert.Empty(c.Validate());
    }

    [Fact]
    public void A_silent_idle_curve_that_starts_at_zero_stays_valid()
    {
        // The whole point of the guard is the danger zone, not silence while cool: the
        // controller ramps this one as the machine heats up, and taking it away would make
        // the app worse at the thing owners actually want a custom curve for.
        var c = new FanCurve(new[]
        {
            new FanCurvePoint(30, 0), new FanCurvePoint(50, 0), new FanCurvePoint(60, 35),
            new FanCurvePoint(80, 70), new FanCurvePoint(90, 100),
        });
        Assert.Empty(c.Validate());
        Assert.True(c.IsValid);
    }

    [Fact]
    public void Safety_errors_come_after_the_structural_ones()
    {
        // The editor and the banner show the first error only. A curve that is both malformed
        // and unsafe needs to say it is malformed first, because that is what the owner has to
        // fix before the safety message even means anything.
        var c = new FanCurve(new[] { new FanCurvePoint(50, 10) });
        Assert.Contains("at least 2", c.Validate()[0]);
    }
}
