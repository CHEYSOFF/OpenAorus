using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Tests;

public class FanControllerTests
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");
    private static Task NoDelay(int _) => Task.CompletedTask;

    private static (FanController ctl, FakeGigabyteWmi wmi) Make(ModelProfile? p = null)
    {
        var wmi = new FakeGigabyteWmi();
        return (new FanController(wmi, p ?? Kd, NoDelay), wmi);
    }

    private static string[] Seq(FakeGigabyteWmi wmi) =>
        wmi.Calls.Select(c => c.Args.Count == 1 ? $"{c.Method}={c.Data}" : c.ToString()).ToArray();

    [Fact]
    public async Task Quiet_sets_only_NvThermalTarget()
    {
        var (ctl, wmi) = Make();
        var r = await ctl.ApplyAsync(FanMode.Quiet);
        Assert.True(r.Success);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetFixedFanStatus=0", "SetStepFanStatus=0",
            "SetAutoFanStatus=0", "SetNvThermalTarget=1",
        }, Seq(wmi));
        Assert.All(wmi.Calls, c => Assert.Equal(WmiClass.Set, c.Class));
    }

    [Fact]
    public async Task Normal_clears_every_flag()
    {
        var (ctl, wmi) = Make();
        await ctl.ApplyAsync(FanMode.Normal);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetFixedFanStatus=0", "SetStepFanStatus=0",
            "SetAutoFanStatus=0", "SetNvThermalTarget=0",
        }, Seq(wmi));
    }

    [Fact]
    public async Task Gaming_enables_auto_fan()
    {
        var (ctl, wmi) = Make();
        await ctl.ApplyAsync(FanMode.Gaming);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetFixedFanStatus=0", "SetStepFanStatus=0",
            "SetAutoFanStatus=1", "SetNvThermalTarget=0",
        }, Seq(wmi));
    }

    [Fact]
    public async Task Turbo_writes_max_duty_then_enables_fixed_and_step()
    {
        var (ctl, wmi) = Make();
        await ctl.ApplyAsync(FanMode.Turbo);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetAutoFanStatus=0", "SetNvThermalTarget=0",
            "SetFixedFanSpeed=229", "SetGPUFanDuty=229",
            "SetStepFanStatus=1", "SetFixedFanStatus=1",
        }, Seq(wmi));
    }

    [Fact]
    public async Task Fixed_scales_percent_to_profile_duty()
    {
        var (ctl, wmi) = Make();
        await ctl.ApplyAsync(FanMode.Fixed, fixedPercent: 50);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetAutoFanStatus=0", "SetNvThermalTarget=0",
            "SetFixedFanSpeed=115", "SetGPUFanDuty=115",
            "SetStepFanStatus=1", "SetFixedFanStatus=1",
        }, Seq(wmi));
    }

    [Fact]
    public async Task Custom_enables_step_mode_then_writes_points_and_terminator()
    {
        var (ctl, wmi) = Make();
        var curve = new FanCurve(new[] { new FanCurvePoint(40, 30), new FanCurvePoint(80, 100) });
        var r = await ctl.ApplyAsync(FanMode.Custom, curve: curve);
        Assert.True(r.Success);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetFixedFanStatus=0", "SetAutoFanStatus=0",
            "SetNvThermalTarget=0", "SetStepFanStatus=1",
            "Set.SetFanIndexValue(Index=0,Temperture=40,Value=69)",
            "Set.SetFanIndexValue(Index=1,Temperture=80,Value=229)",
            "Set.SetFanIndexValue(Index=2,Temperture=0,Value=0)",
        }, Seq(wmi));
    }

    [Fact]
    public async Task Custom_with_15_points_writes_no_terminator()
    {
        var (ctl, wmi) = Make();
        var pts = Enumerable.Range(0, 15).Select(i => new FanCurvePoint(30 + i * 4, Math.Min(100, 20 + i * 6)));
        await ctl.ApplyAsync(FanMode.Custom, curve: new FanCurve(pts));
        Assert.Equal(15, wmi.Calls.Count(c => c.Method == "SetFanIndexValue"));
    }

    [Fact]
    public async Task Custom_with_invalid_curve_fails_without_touching_hardware()
    {
        var (ctl, wmi) = Make();
        var bad = new FanCurve(new[] { new FanCurvePoint(50, 50) });
        var r = await ctl.ApplyAsync(FanMode.Custom, curve: bad);
        Assert.False(r.Success);
        Assert.Contains("at least 2", r.Error);
        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public async Task Custom_without_curve_uses_default()
    {
        var (ctl, wmi) = Make();
        await ctl.ApplyAsync(FanMode.Custom);
        Assert.Equal(FanCurve.Default.Points.Count, wmi.Calls.Count(c => c.Method == "SetFanIndexValue" && c.Args["Temperture"] is byte b && b != 0));
    }

    [Fact]
    public async Task Failure_mid_sequence_stops_and_reports_step()
    {
        var (ctl, wmi) = Make();
        wmi.FailOn.Add("SetAutoFanStatus");
        var r = await ctl.ApplyAsync(FanMode.Gaming);
        Assert.False(r.Success);
        Assert.Contains("SetAutoFanStatus", r.Error);
        Assert.Equal(4, wmi.Calls.Count);
        Assert.Null(ctl.LastApplied);
    }

    [Fact]
    public async Task Read_only_profile_refuses_to_write()
    {
        var (ctl, wmi) = Make(ModelProfile.Detect("ROG Zephyrus"));
        var r = await ctl.ApplyAsync(FanMode.Normal);
        Assert.False(r.Success);
        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public async Task Delay_is_called_between_writes_not_after_last()
    {
        var delays = 0;
        var wmi = new FakeGigabyteWmi();
        var ctl = new FanController(wmi, Kd, _ => { delays++; return Task.CompletedTask; });
        await ctl.ApplyAsync(FanMode.Normal);
        Assert.Equal(wmi.Calls.Count - 1, delays);
    }

    [Fact]
    public async Task Concurrent_applies_are_serialized()
    {
        var wmi = new FakeGigabyteWmi();
        var gate = new SemaphoreSlim(0);
        var ctl = new FanController(wmi, Kd, async _ => await gate.WaitAsync());
        var first = ctl.ApplyAsync(FanMode.Normal);
        var second = ctl.ApplyAsync(FanMode.Quiet);
        Assert.Single(wmi.Calls);           // first sequence blocked in its delay, second waiting
        for (var i = 0; i < 8; i++) gate.Release();
        await first; await second;
        Assert.Equal("SetNvThermalTarget", wmi.Calls[^1].Method);
        Assert.Equal(1, wmi.Calls[^1].Data); // Quiet ran entirely after Normal
        Assert.Equal(FanMode.Quiet, ctl.LastApplied);
    }
}
