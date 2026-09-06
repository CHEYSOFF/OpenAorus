using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// What the curve editor tells the owner when it will not let a curve through.
/// </summary>
/// <remarks>
/// A rejected curve that only greys out Apply leaves the owner dragging points at random. The
/// editor shows the first outstanding problem, so the wording that reaches the panel and the
/// banner has to name the number and the temperature the rule is about, not the rule's name.
/// </remarks>
public class CurveEditorViewModelTests
{
    private static CurveEditorViewModel Editor(params (int T, int D)[] points) =>
        new(points.Select(p => new FanCurvePoint(p.T, p.D)));

    [Fact]
    public void A_curve_that_never_ramps_is_rejected_with_the_number_and_the_temperature()
    {
        var vm = Editor((30, 0), (90, 0));

        Assert.False(vm.IsValid);
        Assert.Contains("60 %", vm.ValidationText);
        Assert.Contains("80 °C", vm.ValidationText);
    }

    [Fact]
    public void A_curve_that_stops_too_low_says_where_it_has_to_reach()
    {
        var vm = Editor((40, 60), (80, 100));

        Assert.False(vm.IsValid);
        Assert.Contains("85 °C", vm.ValidationText);
    }

    [Fact]
    public void A_safe_curve_reads_as_a_plain_point_count()
    {
        var vm = Editor((40, 30), (70, 60), (90, 100));

        Assert.True(vm.IsValid);
        Assert.Equal("3 points", vm.ValidationText);
    }

    [Fact]
    public void Editing_a_point_into_an_unsafe_duty_reports_it_immediately()
    {
        var vm = Editor((40, 30), (80, 70), (90, 100));
        Assert.True(vm.IsValid);

        vm.Points[1].DutyPercent = 30;   // 30 % at 80 °C

        Assert.False(vm.IsValid);
        Assert.Contains("60 %", vm.ValidationText);
    }

    [Fact]
    public void Reset_hands_back_a_curve_that_passes()
    {
        var vm = Editor((30, 0), (90, 0));

        vm.ResetCommand.Execute(null);

        Assert.True(vm.IsValid);
        Assert.Equal(FanCurve.Default.Points, vm.ToCurve().Points);
    }
}
