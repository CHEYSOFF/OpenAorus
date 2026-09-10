using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Until both gates pass, nothing is written to the controller.
/// </summary>
/// <remarks>
/// Gated inside the two controllers rather than at the view models, for the reason the fan floors
/// are: <c>--apply</c> runs from a scheduled task with no window, and it is the path that most
/// needs stopping. On an unregistered machine it produced "step 1/5 setCurrentFanStep failed: not
/// found" with nobody there to read it.
/// </remarks>
public class SchemaWriteGateTests
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");

    [Theory]
    [InlineData(FanMode.Quiet)]
    [InlineData(FanMode.Normal)]
    [InlineData(FanMode.Gaming)]
    [InlineData(FanMode.Turbo)]
    [InlineData(FanMode.Fixed)]
    [InlineData(FanMode.Custom)]
    public async Task No_fan_mode_reaches_the_controller_while_writes_are_locked(FanMode mode)
    {
        var wmi = new FakeGigabyteWmi();
        var fans = new FanController(wmi, Kd, ms => Task.CompletedTask, writesUnlocked: () => false);

        var r = await fans.ApplyAsync(mode);

        Assert.False(r.Success);
        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public async Task The_refusal_names_the_cause_and_where_the_fix_is()
    {
        var fans = new FanController(new FakeGigabyteWmi(), Kd, ms => Task.CompletedTask, () => false);

        var r = await fans.ApplyAsync(FanMode.Normal);

        Assert.Contains("not registered", r.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", r.Error!, StringComparison.Ordinal);
        // Never the symptom the owner saw before this release.
        Assert.DoesNotContain("step 1/", r.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nothing_is_applied_and_nothing_is_announced_as_applied()
    {
        var fans = new FanController(new FakeGigabyteWmi(), Kd, ms => Task.CompletedTask, () => false);
        var applied = 0;
        fans.Applied += _ => applied++;

        await fans.ApplyAsync(FanMode.Turbo);

        Assert.Equal(0, applied);
        Assert.Null(fans.LastApplied);
    }

    [Fact]
    public void No_charge_limit_reaches_the_controller_while_writes_are_locked()
    {
        var wmi = new FakeGigabyteWmi();
        var battery = new BatteryController(wmi, writesUnlocked: () => false);

        var r = battery.SetLimit(enabled: true, stopPercent: 80);

        Assert.False(r.Success);
        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public void The_charge_limit_refusal_names_the_cause_too()
    {
        var r = new BatteryController(new FakeGigabyteWmi(), () => false).SetLimit(true, 80);

        Assert.Contains("not registered", r.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", r.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("SetChargePolicy", r.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_are_never_gated()
    {
        // "Reads and lighting work, writes are refused with a reason." A locked machine must
        // still show temperatures, fan speeds and battery health.
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargePolicy", 0);
        var battery = new BatteryController(wmi, () => false);

        battery.Read();

        Assert.NotEmpty(wmi.Calls);
        Assert.All(wmi.Calls, c => Assert.Equal(WmiClass.Get, c.Class));
    }

    [Fact]
    public void The_sensors_are_never_gated_either()
    {
        // Same rule, read through the class the window actually polls. A machine whose schema is
        // absent answers nothing useful here, but that is the provider's answer and not a refusal
        // this app invented.
        var wmi = new FakeGigabyteWmi();
        new Sensors.SensorReader(wmi, Kd).Read();

        Assert.NotEmpty(wmi.Calls);
        Assert.All(wmi.Calls, c => Assert.Equal(WmiClass.Get, c.Class));
    }

    [Fact]
    public async Task Unlocking_takes_effect_without_a_restart()
    {
        // The owner presses Install, then Check it works, and the fan buttons come alive. A bool
        // captured at construction would need the app restarted.
        var unlocked = false;
        var wmi = new FakeGigabyteWmi();
        var fans = new FanController(wmi, Kd, ms => Task.CompletedTask, () => unlocked);

        Assert.False((await fans.ApplyAsync(FanMode.Normal)).Success);
        unlocked = true;
        Assert.True((await fans.ApplyAsync(FanMode.Normal)).Success);
    }

    [Fact]
    public async Task The_model_gate_and_the_schema_gate_are_both_required()
    {
        var unknown = ModelProfile.Detect("Some Other Laptop");
        var fans = new FanController(new FakeGigabyteWmi(), unknown, ms => Task.CompletedTask, () => true);

        var r = await fans.ApplyAsync(FanMode.Normal);

        Assert.False(r.Success);
        Assert.Contains("not recognised", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Leaving_the_gate_out_keeps_every_existing_call_site_working()
    {
        // Defaulted to open, so every construction site that predates the schema feature - and
        // every test over one - is untouched.
        var fans = new FanController(new FakeGigabyteWmi(), Kd, ms => Task.CompletedTask);

        Assert.True((await fans.ApplyAsync(FanMode.Normal)).Success);
        Assert.True(new BatteryController(new FakeGigabyteWmi()).SetLimit(true, 80).Success);
    }
}
