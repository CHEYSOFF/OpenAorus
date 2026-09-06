using System.IO;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// A settings.json written before the fan floors existed, or hand-edited afterwards, can still
/// hold a Fixed duty of 0 or a curve that never ramps. Nothing between the file and the
/// controller would otherwise object: <c>--apply</c> loads it and writes it.
/// </summary>
/// <remarks>
/// This takes the same route as <see cref="LightingSettings.Repair"/> - repaired inside
/// <see cref="SettingsStore.Load"/> and reported through <c>LastLoadRepaired</c> - so the owner
/// gets one notice covering everything that was changed under them, rather than a second
/// mechanism with its own way of speaking up.
/// </remarks>
public class FanSettingsRepairTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "settings.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private void WriteSettingsFile(string json)
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, json);
    }

    private static string CurveJson(params (int T, int D)[] points) =>
        "[" + string.Join(",", points.Select(p => $"{{\"Temperature\":{p.T},\"DutyPercent\":{p.D}}}")) + "]";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(19)]
    public void A_fixed_duty_below_the_floor_is_raised_to_it(int saved)
    {
        WriteSettingsFile($"{{ \"Mode\": \"Fixed\", \"FixedPercent\": {saved} }}");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        Assert.Equal(FanSafety.MinFixedPercent, loaded.FixedPercent);
        Assert.True(store.LastLoadRepaired);
        Assert.True(store.LastLoadFansRepaired);
        // A single bad value is not a corrupt file: the rest of the owner's settings stay.
        Assert.False(store.LastLoadWasReset);
        Assert.Equal(FanMode.Fixed, loaded.Mode);
    }

    [Fact]
    public void A_fixed_duty_at_or_above_the_floor_is_left_alone()
    {
        WriteSettingsFile("{ \"Mode\": \"Fixed\", \"FixedPercent\": 20 }");
        var store = new SettingsStore(File);
        Assert.Equal(20, store.Load().FixedPercent);
        Assert.False(store.LastLoadRepaired);
        Assert.False(store.LastLoadFansRepaired);
    }

    [Fact]
    public void A_curve_that_never_ramps_is_replaced_with_the_default()
    {
        WriteSettingsFile("{ \"Mode\": \"Custom\", \"Curve\": " + CurveJson((30, 0), (90, 0)) + " }");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        Assert.Equal(FanCurve.Default.Points, loaded.Curve);
        Assert.True(store.LastLoadRepaired);
        Assert.True(store.LastLoadFansRepaired);
        Assert.False(store.LastLoadWasReset);
        Assert.Equal(FanMode.Custom, loaded.Mode);
    }

    [Fact]
    public void A_malformed_curve_is_replaced_with_the_default_too()
    {
        // Duty going backwards is not something a floor can lift; there is nothing to raise it to.
        WriteSettingsFile("{ \"Curve\": " + CurveJson((40, 90), (90, 20)) + " }");
        var store = new SettingsStore(File);

        Assert.Equal(FanCurve.Default.Points, store.Load().Curve);
        Assert.True(store.LastLoadFansRepaired);
    }

    [Fact]
    public void An_empty_curve_is_replaced_with_the_default()
    {
        WriteSettingsFile("{ \"Curve\": [] }");
        var store = new SettingsStore(File);

        Assert.Equal(FanCurve.Default.Points, store.Load().Curve);
        Assert.True(store.LastLoadFansRepaired);
    }

    [Fact]
    public void A_null_curve_is_replaced_with_the_default()
    {
        WriteSettingsFile("{ \"Curve\": null }");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        Assert.Equal(FanCurve.Default.Points, loaded.Curve);
        Assert.True(store.LastLoadFansRepaired);
    }

    [Fact]
    public void A_saved_silent_idle_curve_survives_untouched()
    {
        // The owner's own curve, sitting at 0 % while cool. Replacing this would be the app
        // taking away the thing a custom curve is mostly wanted for.
        var mine = CurveJson((30, 0), (55, 0), (65, 40), (80, 70), (90, 100));
        WriteSettingsFile("{ \"Mode\": \"Custom\", \"Curve\": " + mine + " }");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        Assert.Equal(
            new List<FanCurvePoint> { new(30, 0), new(55, 0), new(65, 40), new(80, 70), new(90, 100) },
            loaded.Curve);
        Assert.False(store.LastLoadRepaired);
        Assert.False(store.LastLoadFansRepaired);
    }

    [Fact]
    public void A_settled_file_is_reported_as_needing_nothing()
    {
        WriteSettingsFile("{ \"Mode\": \"Gaming\", \"FixedPercent\": 55 }");
        var store = new SettingsStore(File);
        store.Load();

        Assert.False(store.LastLoadRepaired);
        Assert.False(store.LastLoadFansRepaired);
        Assert.False(store.LastLoadLightingRepaired);
    }

    [Fact]
    public void A_file_broken_on_both_halves_reports_both()
    {
        WriteSettingsFile(
            "{ \"FixedPercent\": 0, \"Curve\": " + CurveJson((30, 5), (90, 10)) +
            ", \"Lighting\": { \"Effect\": 99 } }");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        Assert.True(store.LastLoadRepaired);
        Assert.True(store.LastLoadFansRepaired);
        Assert.True(store.LastLoadLightingRepaired);
        Assert.Equal(FanSafety.MinFixedPercent, loaded.FixedPercent);
        Assert.Equal(FanCurve.Default.Points, loaded.Curve);
    }

    [Fact]
    public void A_lighting_only_repair_does_not_claim_the_fans_were_touched()
    {
        WriteSettingsFile("{ \"Lighting\": { \"BrightnessPercent\": 500 } }");
        var store = new SettingsStore(File);
        store.Load();

        Assert.True(store.LastLoadLightingRepaired);
        Assert.False(store.LastLoadFansRepaired);
    }

    [Fact]
    public void Repair_leaves_a_healthy_settings_object_alone_and_says_so()
    {
        var settings = new AppSettings { FixedPercent = 50 };
        Assert.False(settings.RepairFans());
        Assert.Equal(FanCurve.Default.Points, settings.Curve);
    }

    [Fact]
    public void Whatever_repair_produces_is_something_the_controller_will_accept()
    {
        var settings = new AppSettings { FixedPercent = 0, Curve = new List<FanCurvePoint> { new(30, 0), new(60, 0) } };
        settings.RepairFans();

        Assert.True(settings.ToCurve().IsValid);
        Assert.True(FanSafety.IsFixedPercentSafe(settings.FixedPercent));
    }
}
