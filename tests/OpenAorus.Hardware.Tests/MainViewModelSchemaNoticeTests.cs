using System.IO;
using OpenAorus.App;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;
using OpenAorus.Hardware.Ui;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// What a machine whose writes are locked says at startup, and how often it says it.
/// </summary>
/// <remarks>
/// Before this, such a machine said nothing until the first write failed - one step into a
/// five-step sequence, naming a method when the whole class was missing. The notice goes through
/// <see cref="BannerState.ReportOverrideNotice"/> for the reason the settings notices do: this is
/// not a per-model condition, so an owner on an unrecognised model has to hear it too.
/// </remarks>
public class MainViewModelSchemaNoticeTests : IDisposable
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    private MainViewModel Build(
        FakeSchemaSystem sys, ModelProfile? profile = null, SchemaRecord? record = null,
        FakeGigabyteWmi? wmi = null)
    {
        profile ??= Kd;
        Directory.CreateDirectory(_dir);

        wmi ??= new FakeGigabyteWmi();
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = new AppSettings();
        if (record is not null) settings.Schema = record;
        var schema = new SchemaService(sys, settings.Schema, SchemaMof.Fingerprint);

        return new MainViewModel(new AppServices
        {
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile, _ => Task.CompletedTask, () => schema.WritesUnlocked),
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi, () => schema.WritesUnlocked),
            Store = store,
            Settings = settings,
            Schema = schema,
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(
                new FakeKeyboardHid(), KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = false,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        });
    }

    /// <summary>A machine as the Control Center uninstaller left it.</summary>
    private static FakeSchemaSystem BareMachine() => new();

    /// <summary>A machine carrying our own registration, whatever has been proved about it.</summary>
    private static FakeSchemaSystem RegisteredMachine()
    {
        var sys = new FakeSchemaSystem();
        foreach (var name in SchemaClasses.All) sys.Classes.Add(name);
        foreach (var (k, v) in SchemaFingerprintTests.LiveFrom(WmiSchemaParserTests.Recovered()))
            sys.MethodIds[k] = v;
        sys.MarkerFingerprint = SchemaMof.Fingerprint;
        return sys;
    }

    /// <summary>A machine with Control Center's own registration on it and no marker of ours.</summary>
    private static FakeSchemaSystem ControlCenterMachine()
    {
        var sys = new FakeSchemaSystem();
        foreach (var name in new[]
                 {
                     WmiSchemaParser.GetClass, WmiSchemaParser.SetClass,
                     WmiSchemaParser.DataClass, WmiSchemaParser.EventClass,
                 })
            sys.Classes.Add(name);
        foreach (var (k, v) in SchemaFingerprintTests.LiveFrom(WmiSchemaParserTests.Recovered()))
            sys.MethodIds[k] = v;
        return sys;
    }

    /// <summary>The record a completed gate run leaves behind.</summary>
    /// <param name="registered">Whether this app installed what was proved. False on a Control
    /// Center machine, where the run registers nothing and the pass is over somebody else's
    /// classes - which is a state the record has to be able to hold.</param>
    private static SchemaRecord Proved(bool registered = true) => new()
    {
        Registered = registered,
        GatesPassed = true,
        Fingerprint = SchemaMof.Fingerprint,
        When = DateTime.Now,
        GateSummary = "Gate A: passed; Gate B: passed",
    };

    [Fact]
    public void A_machine_with_no_schema_is_told_at_startup_rather_than_at_the_first_failed_write()
    {
        var vm = Build(BareMachine());

        Assert.Equal(BannerKind.Warning, vm.Banner);
        Assert.Contains("not registered", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", vm.BannerText, StringComparison.Ordinal);
        Assert.DoesNotContain("step 1/", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", vm.BannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task It_is_said_once_and_never_again_by_the_poll_loop()
    {
        var vm = Build(BareMachine());
        Assert.Contains("not registered", vm.BannerText, StringComparison.OrdinalIgnoreCase);

        var raisedAgain = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.BannerText) &&
                vm.BannerText.Contains("not registered", StringComparison.OrdinalIgnoreCase))
                raisedAgain++;
        };

        for (var i = 0; i < 5; i++)
            await vm.OnSensorPollAsync(SensorSnapshot.Empty with { Ok = true, Error = null }, 1000 * i);

        Assert.Equal(0, raisedAgain);
    }

    [Fact]
    public async Task The_explanation_survives_the_sensor_failures_the_missing_schema_causes()
    {
        // The whole point of the notice, and the one second it used to last. Without a schema
        // there is no GB_WMIACPI_Get either, so every sensor poll fails as well - and the poll
        // loop's report of that used to take the explanation off the screen a second after
        // startup, leaving the owner with the method-level noise this release exists to replace.
        var wmi = new FakeGigabyteWmi();
        foreach (var m in new[] { "getCpuTemp", "getGpuTemp1", "GetThermalData", "getRpm1", "getRpm2" })
            wmi.FailOn.Add(m);

        var vm = Build(BareMachine(), wmi: wmi);
        Assert.Contains("not registered", vm.BannerText, StringComparison.OrdinalIgnoreCase);

        // Exactly what the poller would hand it on such a machine, read through the real reader.
        var snap = new SensorReader(wmi, Kd).Read();
        Assert.False(snap.Ok);
        Assert.Contains("getCpuTemp", snap.Error!, StringComparison.Ordinal);

        for (var i = 0; i < 3; i++) await vm.OnSensorPollAsync(snap, 1000 * i);

        Assert.Equal(BannerKind.Warning, vm.Banner);
        Assert.Contains("not registered", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("getCpuTemp", vm.BannerText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fan_strip_and_the_battery_card_are_disabled_rather_than_offered()
    {
        var vm = Build(BareMachine());

        Assert.False(vm.CanWrite);
        Assert.False(vm.CanChangeMode);
        Assert.False(vm.Battery.CanWrite);
        Assert.False(vm.SettingsVm.CanWrite);
    }

    [Fact]
    public void A_registration_nothing_has_proved_yet_is_named_as_that_rather_than_as_missing()
    {
        var vm = Build(RegisteredMachine());

        Assert.Equal(BannerKind.Warning, vm.Banner);
        Assert.Contains("read check", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CanWrite);
    }

    [Fact]
    public void A_registration_both_gates_passed_says_nothing_and_unlocks_everything()
    {
        var vm = Build(RegisteredMachine(), record: Proved());

        Assert.Equal(BannerKind.None, vm.Banner);
        Assert.Equal("", vm.BannerText);
        Assert.True(vm.CanWrite);
        Assert.True(vm.Battery.CanWrite);
        Assert.True(vm.SettingsVm.CanWrite);
    }

    [Fact]
    public void A_control_center_machine_is_told_what_is_missing_is_the_checks_and_not_the_registration()
    {
        // The state this app is normally installed into. The banner has to name the way out, and
        // the way out is not registering anything: Install is refused here and always will be.
        var vm = Build(ControlCenterMachine());

        Assert.Equal(BannerKind.Warning, vm.Banner);
        Assert.False(vm.CanWrite);
        Assert.Contains("Settings", vm.BannerText, StringComparison.Ordinal);
        Assert.Contains("read check", vm.BannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not registered", vm.BannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_control_center_machine_that_has_passed_both_checks_says_nothing_and_unlocks_everything()
    {
        // The regression, as the owner met it: fan and battery control gone on a machine that had
        // worked for weeks, with no button on the card that could give it back.
        var vm = Build(ControlCenterMachine(), record: Proved(registered: false));

        Assert.Equal(BannerKind.None, vm.Banner);
        Assert.Equal("", vm.BannerText);
        Assert.True(vm.CanWrite);
        Assert.True(vm.Battery.CanWrite);
        Assert.True(vm.SettingsVm.CanWrite);

        // And still nobody's registration to install over or take away.
        Assert.False(vm.SettingsVm.Schema.CanInstall);
        Assert.False(vm.SettingsVm.Schema.CanRemove);
        Assert.True(vm.SettingsVm.Schema.CanRunGates);
    }

    [Fact]
    public void An_unrecognised_model_hears_it_too()
    {
        // The permanent read-only banner would otherwise swallow this, which is exactly what
        // ReportOverrideNotice exists to prevent.
        var vm = Build(BareMachine(), ModelProfile.Detect("Some Other Laptop"));

        Assert.Contains("not registered", vm.BannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_rig_that_says_nothing_about_the_schema_is_left_exactly_as_it_was()
    {
        // AppServices.Schema defaults to a service over no machine at all, so every construction
        // site that predates this feature keeps its unlocked, silent behaviour.
        Directory.CreateDirectory(_dir);
        var wmi = new FakeGigabyteWmi();
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));

        var vm = new MainViewModel(new AppServices
        {
            Profile = Kd,
            Wmi = wmi,
            Fans = new FanController(wmi, Kd, _ => Task.CompletedTask),
            Sensors = new SensorReader(wmi, Kd),
            Battery = new BatteryController(wmi),
            Store = store,
            Settings = new AppSettings(),
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(
                new FakeKeyboardHid(), KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = false,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        });

        Assert.Equal(BannerKind.None, vm.Banner);
        Assert.True(vm.CanWrite);
    }
}
