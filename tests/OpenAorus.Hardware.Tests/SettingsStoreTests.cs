using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "settings.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var s = new SettingsStore(File).Load();
        Assert.Equal(FanMode.Normal, s.Mode);
        Assert.Equal(50, s.FixedPercent);
        Assert.Equal(FanCurve.Default.Points, s.Curve);
        Assert.False(s.ChargeLimitEnabled);
        Assert.Equal(80, s.ChargeStopPercent);
        Assert.Equal(1000, s.PollIntervalVisibleMs);
        Assert.Equal(5000, s.PollIntervalHiddenMs);
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var store = new SettingsStore(File);
        var s = store.Load();
        s.Mode = FanMode.Custom;
        s.FixedPercent = 73;
        // Has to clear FanSafety now, or Load repairs it away and the round trip is not one.
        // The old fixture ran 20 % at 80 °C.
        s.Curve = new List<FanCurvePoint> { new(45, 20), new(80, 70), new(90, 100) };
        s.ChargeLimitEnabled = true;
        s.ChargeStopPercent = 60;
        s.StartWithWindows = true;
        store.Save(s);

        var back = new SettingsStore(File).Load();
        Assert.Equal(FanMode.Custom, back.Mode);
        Assert.Equal(73, back.FixedPercent);
        Assert.Equal(s.Curve, back.Curve);
        Assert.True(back.ChargeLimitEnabled);
        Assert.Equal(60, back.ChargeStopPercent);
        Assert.True(back.StartWithWindows);
    }

    [Fact]
    public void Save_creates_directory_and_writes_enum_as_string()
    {
        var store = new SettingsStore(File);
        store.Save(new AppSettings { Mode = FanMode.Gaming });
        Assert.True(System.IO.File.Exists(File));
        Assert.Contains("\"Gaming\"", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void Corrupt_file_is_renamed_to_bad_and_defaults_returned()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ not json");
        var store = new SettingsStore(File);
        var s = store.Load();
        Assert.Equal(FanMode.Normal, s.Mode);
        Assert.True(System.IO.File.Exists(File + ".bad"));
        Assert.False(System.IO.File.Exists(File));
        Assert.True(store.LastLoadWasReset);
    }

    [Fact]
    public void Load_reports_no_reset_on_a_clean_load()
    {
        var store = new SettingsStore(File);
        store.Load();
        Assert.False(store.LastLoadWasReset);
    }

    [Fact]
    public void Unreadable_file_resets_to_defaults_instead_of_throwing()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{}");
        var store = new SettingsStore(File);
        AppSettings? result = null;

        // A locked file throws IOException on read, not JsonException - the fix broadens the catch to cover it.
        using (new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Record.Exception(() => result = store.Load());
            Assert.Null(ex);
        }

        Assert.NotNull(result);
        Assert.Equal(FanMode.Normal, result!.Mode);
        Assert.True(store.LastLoadWasReset);
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ \"Mode\": \"Quiet\", \"Future\": 1 }");
        Assert.Equal(FanMode.Quiet, new SettingsStore(File).Load().Mode);
    }

    [Fact]
    public void A_hand_edited_gate_pass_is_repaired_away_on_load()
    {
        // A settings file is a text file anyone can edit, and the gate record is the thing that
        // unlocks writing to the firmware. It is repaired at the one choke point every other
        // section is repaired at, so there is no second way in.
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ \"Schema\": { \"GatesPassed\": true } }");
        var store = new SettingsStore(File);

        var s = store.Load();

        Assert.False(s.Schema.GatesPassed);
        Assert.True(store.LastLoadSchemaRepaired);
        Assert.True(store.LastLoadRepaired);
    }

    [Fact]
    public void A_recorded_gate_pass_survives_a_round_trip_untouched()
    {
        var store = new SettingsStore(File);
        var s = store.Load();
        s.Schema.Registered = true;
        s.Schema.GatesPassed = true;
        s.Schema.Fingerprint = "aaa";
        s.Schema.When = DateTime.Now;
        s.Schema.GateSummary = "Gate A: passed, 69 methods compared, 0 warnings";
        store.Save(s);

        var reloaded = new SettingsStore(File);
        var back = reloaded.Load();

        Assert.True(back.Schema.GatesPassed);
        Assert.Equal("aaa", back.Schema.Fingerprint);
        Assert.Equal(s.Schema.GateSummary, back.Schema.GateSummary);
        Assert.False(reloaded.LastLoadSchemaRepaired);
        Assert.False(reloaded.LastLoadRepaired);
    }

    [Fact]
    public void A_file_from_before_the_gates_existed_has_a_record_that_claims_nothing()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ \"Mode\": \"Quiet\" }");
        var store = new SettingsStore(File);

        var s = store.Load();

        Assert.NotNull(s.Schema);
        Assert.False(s.Schema.GatesPassed);
        Assert.False(store.LastLoadSchemaRepaired);
    }
}
