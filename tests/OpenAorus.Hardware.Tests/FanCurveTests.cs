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
}
