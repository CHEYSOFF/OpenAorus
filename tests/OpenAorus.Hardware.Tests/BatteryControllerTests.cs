using OpenAorus.Hardware.Battery;

namespace OpenAorus.Hardware.Tests;

public class BatteryControllerTests
{
    [Fact]
    public void SetLimit_enabled_writes_policy_4_then_stop()
    {
        var wmi = new FakeGigabyteWmi();
        var r = new BatteryController(wmi).SetLimit(true, 80);
        Assert.True(r.Success);
        Assert.Equal(new[] { "SetChargePolicy=4", "SetChargeStop=80" }, wmi.Calls.Select(c => $"{c.Method}={c.Data}"));
    }

    [Fact]
    public void SetLimit_disabled_writes_policy_0_and_stop_100()
    {
        var wmi = new FakeGigabyteWmi();
        new BatteryController(wmi).SetLimit(false, 70);
        Assert.Equal(new[] { "SetChargePolicy=0", "SetChargeStop=100" }, wmi.Calls.Select(c => $"{c.Method}={c.Data}"));
    }

    [Theory]
    [InlineData(10, 60)]
    [InlineData(59, 60)]
    [InlineData(101, 100)]
    public void SetLimit_clamps_stop_to_60_100(int requested, int written)
    {
        var wmi = new FakeGigabyteWmi();
        new BatteryController(wmi).SetLimit(true, requested);
        Assert.Equal(written, wmi.Calls[1].Data);
    }

    [Fact]
    public void SetLimit_reports_failed_step()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.FailOn.Add("SetChargeStop");
        var r = new BatteryController(wmi).SetLimit(true, 80);
        Assert.False(r.Success);
        Assert.Contains("SetChargeStop", r.Error);
    }

    [Fact]
    public void Read_maps_policy_stop_cycles_health()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargePolicy", (ushort)4);
        wmi.Respond("GetChargeStop", (ushort)80);
        wmi.Respond("GetBatteryCount", (ushort)123);
        wmi.Respond("GetBatteryHealth", (byte)2);
        var s = new BatteryController(wmi).Read();
        Assert.True(s.Ok);
        Assert.True(s.CustomLimitEnabled);
        Assert.Equal(80, s.StopPercent);
        Assert.Equal(123, s.CycleCount);
        Assert.Equal(2, s.Health);
    }

    [Fact]
    public void Read_policy_0_means_standard()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargePolicy", (ushort)0);
        wmi.Respond("GetChargeStop", (ushort)100);
        Assert.False(new BatteryController(wmi).Read().CustomLimitEnabled);
    }

    [Fact]
    public void Read_failure_is_reported()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.FailOn.Add("GetChargePolicy");
        var s = new BatteryController(wmi).Read();
        Assert.False(s.Ok);
        Assert.Contains("GetChargePolicy", s.Error);
    }
}
