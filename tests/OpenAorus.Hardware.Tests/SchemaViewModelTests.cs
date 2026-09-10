using System.IO;
using OpenAorus.App;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The Settings card that registers the interface, proves it, and takes it back out.
/// </summary>
/// <remarks>
/// <para>
/// It never decides anything itself. <see cref="SchemaState"/> says which of the three actions a
/// machine is entitled to and <see cref="SchemaRegistrar"/> re-decides at the moment of acting;
/// what is checked here is that the card offers exactly what those two permit, and that every
/// refusal names a cause rather than the "not found" the owner used to be shown.
/// </para>
/// <para>
/// The machine that matters most here is the one carrying Control Center's own registration, which
/// is what most laptops this app is installed on are carrying. Register and Remove are refused
/// there and always will be, so the check has to be offered - it is the whole of that owner's route
/// to a working app, and withholding it left them read-only with no button that could help.
/// </para>
/// </remarks>
public class SchemaViewModelTests : IDisposable
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");
    private static readonly string Fingerprint = SchemaMof.Fingerprint;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    /// <summary>One card, the machine behind it, and how often it told the window to re-read.</summary>
    private sealed class Rig
    {
        public SchemaViewModel Vm { get; set; } = null!;
        public FakeSchemaSystem Sys { get; init; } = null!;
        public AppServices Services { get; init; } = null!;
        public int Changes { get; set; }
    }

    private Rig Build(FakeSchemaSystem sys, FakeGigabyteWmi? wmi = null, bool elevated = true)
    {
        wmi ??= new FakeGigabyteWmi();
        Directory.CreateDirectory(_dir);

        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = new AppSettings();
        var schema = new SchemaService(sys, settings.Schema, Fingerprint);

        var services = new AppServices
        {
            Profile = Kd,
            Wmi = wmi,
            Fans = new FanController(wmi, Kd, _ => Task.CompletedTask, () => schema.WritesUnlocked),
            Sensors = new SensorReader(wmi, Kd),
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
        };

        var rig = new Rig { Sys = sys, Services = services };
        rig.Vm = new SchemaViewModel(services, () => rig.Changes++, () => elevated);
        return rig;
    }

    /// <summary>A machine the owner's Control Center uninstaller left behind: nothing at all.</summary>
    private static FakeSchemaSystem EmptyMachine()
    {
        var sys = new FakeSchemaSystem();
        sys.CompileForReal(Fingerprint, SchemaFingerprintTests.LiveFrom(WmiSchemaParserTests.Recovered()));
        return sys;
    }

    /// <summary>A machine with Control Center's own registration on it.</summary>
    private static FakeSchemaSystem ControlCenterMachine()
    {
        var sys = new FakeSchemaSystem();
        foreach (var name in new[] { "GB_WMIACPI_Get", "GB_WMIACPI_Set", "GB_WMIACPI_Data", "GB_WMIACPI_Event" })
            sys.Classes.Add(name);
        foreach (var (k, v) in SchemaFingerprintTests.LiveFrom(WmiSchemaParserTests.Recovered()))
            sys.MethodIds[k] = v;
        return sys;
    }

    [Fact]
    public void A_machine_with_nothing_on_it_is_offered_the_install_and_nothing_else()
    {
        var rig = Build(EmptyMachine());

        Assert.Equal(SchemaStatus.Absent, rig.Vm.Status);
        Assert.True(rig.Vm.CanInstall);
        Assert.False(rig.Vm.CanRemove);
        Assert.False(rig.Vm.CanRunGates);
        Assert.False(rig.Services.Schema.WritesUnlocked);
    }

    [Fact]
    public void A_machine_control_center_registered_is_offered_the_check_and_neither_other_button()
    {
        // Install and Remove are refused here and always will be, so the check is the only
        // button on the card - and the only one this owner needs, because what a passing run
        // unlocks is the writes and not the registration.
        var rig = Build(ControlCenterMachine());

        Assert.Equal(SchemaStatus.Foreign, rig.Vm.Status);
        Assert.True(rig.Vm.CanRunGates);
        Assert.False(rig.Vm.CanInstall);
        Assert.False(rig.Vm.CanRemove);
        Assert.False(rig.Services.Schema.WritesUnlocked);
    }

    [Fact]
    public async Task Running_the_gates_on_a_control_center_machine_unlocks_writes_and_registers_nothing()
    {
        var rig = Build(ControlCenterMachine(), HealthyController());

        await rig.Vm.RunGatesCommand.ExecuteAsync(null);

        Assert.Equal(SchemaStatus.ForeignGated, rig.Vm.Status);
        Assert.True(rig.Services.Schema.WritesUnlocked);
        Assert.False(rig.Vm.CanInstall);
        Assert.False(rig.Vm.CanRemove);
        Assert.Empty(rig.Sys.MofCompCalls);
        Assert.Empty(rig.Sys.FilesWritten);
        // Nothing was registered, so nothing claims to have been. The pass stands on the
        // fingerprint alone, which is the only thing that could vouch for it anyway.
        Assert.False(rig.Services.Settings.Schema.Registered);
        Assert.True(rig.Services.Settings.Schema.GatesPassed);
        Assert.Equal(Fingerprint, rig.Services.Settings.Schema.Fingerprint);
    }

    [Fact]
    public async Task A_pass_over_control_centers_own_schema_expires_the_moment_that_schema_changes()
    {
        var sys = ControlCenterMachine();
        var rig = Build(sys, HealthyController());
        await rig.Vm.RunGatesCommand.ExecuteAsync(null);
        Assert.True(rig.Services.Schema.WritesUnlocked);

        // Control Center updates itself: every name still there, one of them now reaching a
        // different firmware method. Every id the gates proved is an id nobody has checked.
        sys.MethodIds[WmiSchemaParser.GetClass] =
            new Dictionary<string, int>(sys.MethodIds[WmiSchemaParser.GetClass], StringComparer.Ordinal)
            {
                ["GetCPUFanDuty"] = 71,
            };

        await rig.Vm.RefreshAsync();

        Assert.Equal(SchemaStatus.Foreign, rig.Vm.Status);
        Assert.False(rig.Services.Schema.WritesUnlocked);
    }

    [Fact]
    public async Task Install_and_remove_stay_refused_on_a_control_center_machine_that_has_passed_the_gates()
    {
        var rig = Build(ControlCenterMachine(), HealthyController());
        await rig.Vm.RunGatesCommand.ExecuteAsync(null);

        await rig.Vm.InstallCommand.ExecuteAsync(null);
        await rig.Vm.RemoveCommand.ExecuteAsync(null);

        Assert.Empty(rig.Sys.MofCompCalls);
        Assert.Empty(rig.Sys.FilesDeleted);
        Assert.Equal(SchemaStatus.ForeignGated, rig.Vm.Status);
        Assert.True(rig.Services.Schema.WritesUnlocked);
    }

    [Fact]
    public async Task Install_on_a_machine_control_center_registered_does_nothing_and_says_why()
    {
        var rig = Build(ControlCenterMachine());

        await rig.Vm.InstallCommand.ExecuteAsync(null);

        Assert.Empty(rig.Sys.FilesWritten);
        Assert.Empty(rig.Sys.MofCompCalls);
        Assert.Contains("registered by something else", rig.Vm.Message, StringComparison.Ordinal);
        Assert.Equal(SchemaStatus.Foreign, rig.Vm.Status);
    }

    [Fact]
    public async Task A_successful_install_leaves_the_registration_ours_and_writes_still_locked()
    {
        var rig = Build(EmptyMachine());

        await rig.Vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(SchemaStatus.Ours, rig.Vm.Status);
        Assert.False(rig.Services.Schema.WritesUnlocked);
        // Registering is not proving, so the next thing offered is the check and not another install.
        Assert.False(rig.Vm.CanInstall);
        Assert.True(rig.Vm.CanRunGates);
        Assert.True(rig.Vm.CanRemove);
        Assert.True(rig.Services.Settings.Schema.Registered);
        Assert.False(rig.Services.Settings.Schema.GatesPassed);
    }

    [Fact]
    public async Task Running_the_gates_on_a_healthy_machine_is_what_unlocks_writes()
    {
        var rig = Build(EmptyMachine(), HealthyController());
        await rig.Vm.InstallCommand.ExecuteAsync(null);

        await rig.Vm.RunGatesCommand.ExecuteAsync(null);

        Assert.Equal(SchemaStatus.OursGated, rig.Vm.Status);
        Assert.True(rig.Services.Schema.WritesUnlocked);
        Assert.True(rig.Services.Settings.Schema.GatesPassed);
        Assert.Equal(Fingerprint, rig.Services.Settings.Schema.Fingerprint);
        Assert.NotNull(rig.Services.Settings.Schema.When);
    }

    [Fact]
    public async Task A_failing_gate_a_leaves_writes_locked_and_puts_the_first_failure_in_the_message()
    {
        // A controller that answers everything, including the thirty methods the firmware must
        // refuse - which is what a wrong id mapping looks like from the outside.
        var rig = Build(EmptyMachine(), new FakeGigabyteWmi());
        await rig.Vm.InstallCommand.ExecuteAsync(null);

        await rig.Vm.RunGatesCommand.ExecuteAsync(null);

        Assert.Equal(SchemaStatus.Ours, rig.Vm.Status);
        Assert.False(rig.Services.Schema.WritesUnlocked);
        Assert.False(rig.Services.Settings.Schema.GatesPassed);
        Assert.Contains("Gate A", rig.Vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_gate_a_never_writes_to_the_firmware()
    {
        var wmi = new FakeGigabyteWmi();
        var rig = Build(EmptyMachine(), wmi);
        await rig.Vm.InstallCommand.ExecuteAsync(null);

        await rig.Vm.RunGatesCommand.ExecuteAsync(null);

        Assert.DoesNotContain(wmi.Calls, c => c.Class == Wmi.WmiClass.Set);
    }

    [Fact]
    public async Task Remove_takes_our_own_registration_away_and_forgets_the_pass()
    {
        var rig = Build(EmptyMachine(), HealthyController());
        await rig.Vm.InstallCommand.ExecuteAsync(null);
        await rig.Vm.RunGatesCommand.ExecuteAsync(null);

        await rig.Vm.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(SchemaStatus.Absent, rig.Vm.Status);
        Assert.False(rig.Services.Schema.WritesUnlocked);
        Assert.False(rig.Services.Settings.Schema.Registered);
        Assert.False(rig.Services.Settings.Schema.GatesPassed);
        Assert.True(rig.Vm.CanInstall);
    }

    [Fact]
    public async Task Remove_on_a_machine_control_center_registered_deletes_nothing()
    {
        // The remove file deletes by name and the names are Gigabyte's, so this is the refusal
        // that matters most on this card.
        var rig = Build(ControlCenterMachine());

        await rig.Vm.RemoveCommand.ExecuteAsync(null);

        Assert.Empty(rig.Sys.MofCompCalls);
        Assert.Equal(SchemaStatus.Foreign, rig.Vm.Status);
    }

    [Fact]
    public async Task Nothing_the_card_says_is_ever_the_symptom_the_owner_was_shown()
    {
        foreach (var machine in new[] { EmptyMachine(), ControlCenterMachine() })
        {
            var rig = Build(machine);

            Assert.DoesNotContain("not found", rig.Vm.StatusText, StringComparison.OrdinalIgnoreCase);

            await rig.Vm.InstallCommand.ExecuteAsync(null);
            Assert.DoesNotContain("not found", rig.Vm.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("step 1/", rig.Vm.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_card_says_administrator_rights_are_missing_rather_than_just_failing()
    {
        var rig = Build(EmptyMachine(), elevated: false);

        Assert.True(rig.Vm.NeedsElevation);
    }

    [Fact]
    public async Task An_unelevated_install_changes_nothing_and_says_so()
    {
        var rig = Build(EmptyMachine(), elevated: false);

        await rig.Vm.InstallCommand.ExecuteAsync(null);

        Assert.Empty(rig.Sys.MofCompCalls);
        Assert.Contains("administrator", rig.Vm.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SchemaStatus.Absent, rig.Vm.Status);
    }

    [Fact]
    public async Task The_state_changing_while_the_window_is_open_reaches_the_bindings()
    {
        // The owner presses Install, then Check it works, and the fan strip has to come alive
        // without a restart. Both halves are pinned: the card's own bindings, and the callback
        // the surrounding view models re-raise CanWrite from.
        var rig = Build(EmptyMachine(), HealthyController());
        var raised = new List<string>();
        rig.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        await rig.Vm.InstallCommand.ExecuteAsync(null);
        await rig.Vm.RunGatesCommand.ExecuteAsync(null);

        Assert.Contains(nameof(SchemaViewModel.Status), raised);
        Assert.Contains(nameof(SchemaViewModel.CanInstall), raised);
        Assert.Contains(nameof(SchemaViewModel.CanRemove), raised);
        Assert.Contains(nameof(SchemaViewModel.StatusText), raised);
        Assert.True(rig.Changes >= 2, $"the surrounding view models were told {rig.Changes} times");
    }

    [Fact]
    public async Task Reopening_the_dialog_re_reads_a_machine_that_moved_underneath_it()
    {
        // Control Center can be installed while this app sits in the tray. A card still offering
        // Install from a reading taken at startup is a button that refuses after it is pressed.
        var sys = EmptyMachine();
        var rig = Build(sys);
        Assert.True(rig.Vm.CanInstall);

        foreach (var name in new[] { "GB_WMIACPI_Get", "GB_WMIACPI_Set", "GB_WMIACPI_Data", "GB_WMIACPI_Event" })
            sys.Classes.Add(name);
        foreach (var (k, v) in SchemaFingerprintTests.LiveFrom(WmiSchemaParserTests.Recovered()))
            sys.MethodIds[k] = v;

        await rig.Vm.RefreshAsync();

        Assert.Equal(SchemaStatus.Foreign, rig.Vm.Status);
        Assert.False(rig.Vm.CanInstall);
    }

    [Fact]
    public void The_card_explains_the_state_in_the_state_machine_s_own_words()
    {
        // Not a second copy of the prose. An explanation that drifted from SchemaState.Explain
        // would be the window and the refusal disagreeing about the same machine.
        var rig = Build(ControlCenterMachine());

        Assert.Equal(SchemaState.Explain(SchemaStatus.Foreign), rig.Vm.StatusText);
    }

    [Fact]
    public void A_repository_that_will_not_answer_offers_nothing_and_locks_writes()
    {
        var sys = EmptyMachine();
        sys.LookFailure = new InvalidOperationException("the WMI service is restarting");
        var rig = Build(sys);

        Assert.Null(rig.Vm.Status);
        Assert.False(rig.Vm.CanInstall);
        Assert.False(rig.Vm.CanRemove);
        Assert.False(rig.Vm.CanRunGates);
        Assert.False(rig.Services.Schema.WritesUnlocked);
        Assert.Contains("could not read", rig.Vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A controller answering exactly what the known-good dump recorded.</summary>
    private static FakeGigabyteWmi HealthyController()
    {
        var wmi = new FakeGigabyteWmi();
        foreach (var m in KnownGoodReading.Reference.Methods)
        {
            if (!m.Answered) { wmi.FailOn.Add(m.Method); continue; }
            wmi.Responses[m.Method] = m.Values.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }
        wmi.FanTable = KnownGoodReading.Reference.FanTable.ToDictionary(s => s.Index, s => (s.Temperature, s.Duty));
        return wmi;
    }
}
