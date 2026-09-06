using Microsoft.Win32;
using OpenAorus.Hardware.Platform;

namespace OpenAorus.Hardware.Tests;

public sealed class FakeGccSystem : IGccSystem
{
    public HashSet<string> Tasks { get; } = new() { "GCC" };
    public HashSet<string> DisabledTasks { get; } = new();
    public Dictionary<string, string> RunValues { get; } = new() { ["AorusFusion"] = @"C:\Program Files\ControlCenter\FusionStartUp.exe" };
    public Dictionary<string, RegistryValueKind> RunValueKinds { get; } = new() { ["AorusFusion"] = RegistryValueKind.ExpandString };

    /// <summary>Services keyed by name -> current start mode. Absence of a key means the service does not exist.</summary>
    public Dictionary<string, GccServiceStartMode> ServiceStartModes { get; } = new() { ["SMV4_Service"] = GccServiceStartMode.Automatic };
    public HashSet<string> RunningServices { get; } = new() { "SMV4_Service" };

    /// <summary>Names for which StopAndDisableService should report failure (the "sc config" call failed), without changing state.</summary>
    public HashSet<string> DisableServiceShouldFail { get; } = new();

    public HashSet<string> Running { get; } = new() { "GCC", "FusionStation" };
    public List<string> Log { get; } = new();

    public bool TaskExists(string name) => Tasks.Contains(name);
    public bool IsTaskEnabled(string name) => Tasks.Contains(name) && !DisabledTasks.Contains(name);
    public bool DisableTask(string name) { Log.Add($"disable-task {name}"); return DisabledTasks.Add(name); }
    public bool EnableTask(string name) { Log.Add($"enable-task {name}"); return DisabledTasks.Remove(name); }
    public string? ReadRunValue(string name) => RunValues.GetValueOrDefault(name);
    public RegistryValueKind ReadRunValueKind(string name) => RunValueKinds.GetValueOrDefault(name, RegistryValueKind.String);
    public void DeleteRunValue(string name) { Log.Add($"delete-run {name}"); RunValues.Remove(name); RunValueKinds.Remove(name); }
    public void WriteRunValue(string name, string value, RegistryValueKind kind) { Log.Add($"write-run {name}"); RunValues[name] = value; RunValueKinds[name] = kind; }

    public bool ServiceExists(string name) => ServiceStartModes.ContainsKey(name);
    public GccServiceStartMode GetServiceStartMode(string name) => ServiceStartModes.GetValueOrDefault(name, GccServiceStartMode.Disabled);
    public bool IsServiceRunning(string name) => RunningServices.Contains(name);

    public bool StopAndDisableService(string name)
    {
        Log.Add($"disable-service {name}");
        if (DisableServiceShouldFail.Contains(name)) return false;
        RunningServices.Remove(name);
        ServiceStartModes[name] = GccServiceStartMode.Disabled;
        return true;
    }

    public bool EnableService(string name, GccServiceStartMode originalMode, bool wasRunning)
    {
        Log.Add($"enable-service {name} {originalMode} running={wasRunning}");
        ServiceStartModes[name] = originalMode;
        if (wasRunning) RunningServices.Add(name);
        return true;
    }

    public int KillProcesses(IEnumerable<string> names) { var n = Running.RemoveWhere(names.Contains); Log.Add($"kill {n}"); return n; }
    public bool AnyProcessRunning(IEnumerable<string> names) => Running.Overlaps(names);
}

public class GccTakeoverTests
{
    [Fact]
    public void TakeOver_disables_task_run_key_service_and_kills_processes()
    {
        var sys = new FakeGccSystem();
        var state = GccTakeover.TakeOver(sys);

        Assert.True(state.TaskDisabled);
        Assert.Equal(@"C:\Program Files\ControlCenter\FusionStartUp.exe", state.RunValue);
        Assert.True(state.ServiceDisabled);
        Assert.Equal(GccServiceStartMode.Automatic, state.ServiceStartMode);
        Assert.True(state.ServiceWasRunning);
        Assert.Contains("GCC", sys.DisabledTasks);
        Assert.DoesNotContain("AorusFusion", sys.RunValues.Keys);
        Assert.Equal(GccServiceStartMode.Disabled, sys.ServiceStartModes["SMV4_Service"]);
        Assert.Empty(sys.Running);
        Assert.False(GccTakeover.IsGccActive(sys));
    }

    [Fact]
    public void TakeOver_records_only_what_existed()
    {
        var sys = new FakeGccSystem();
        sys.Tasks.Clear(); sys.RunValues.Clear(); sys.ServiceStartModes.Clear();
        var state = GccTakeover.TakeOver(sys);
        Assert.False(state.TaskDisabled);
        Assert.Null(state.RunValue);
        Assert.False(state.ServiceDisabled);
        Assert.DoesNotContain(sys.Log, l => l.StartsWith("disable"));
    }

    [Fact]
    public void Restore_reverses_exactly_the_recorded_changes()
    {
        var sys = new FakeGccSystem();
        var state = GccTakeover.TakeOver(sys);
        sys.Log.Clear();

        GccTakeover.Restore(sys, state);

        Assert.Empty(sys.DisabledTasks);
        Assert.Equal(@"C:\Program Files\ControlCenter\FusionStartUp.exe", sys.RunValues["AorusFusion"]);
        Assert.Equal(GccServiceStartMode.Automatic, sys.ServiceStartModes["SMV4_Service"]);
        Assert.Contains("SMV4_Service", sys.RunningServices);
        Assert.Equal(new[] { "enable-task GCC", "write-run AorusFusion", $"enable-service SMV4_Service {GccServiceStartMode.Automatic} running=True" }, sys.Log);
    }

    [Fact]
    public void Restore_with_partial_state_skips_untouched_items()
    {
        var sys = new FakeGccSystem();
        GccTakeover.Restore(sys, new GccTakeoverState { TaskDisabled = true });
        Assert.Equal(new[] { "enable-task GCC" }, sys.Log);
    }

    [Fact]
    public void IsGccActive_true_when_task_enabled_or_process_running()
    {
        var sys = new FakeGccSystem();
        Assert.True(GccTakeover.IsGccActive(sys));
        sys.DisabledTasks.Add("GCC"); sys.RunValues.Clear(); sys.ServiceStartModes["SMV4_Service"] = GccServiceStartMode.Disabled;
        Assert.True(GccTakeover.IsGccActive(sys));   // processes still running
        sys.Running.Clear();
        Assert.False(GccTakeover.IsGccActive(sys));
    }

    [Fact]
    public void TakeOver_does_not_record_items_the_owner_already_disabled()
    {
        var sys = new FakeGccSystem();
        sys.DisabledTasks.Add("GCC");
        sys.ServiceStartModes["SMV4_Service"] = GccServiceStartMode.Disabled;
        sys.RunValues.Clear(); // isolate this test to the task/service bookkeeping under review

        var state = GccTakeover.TakeOver(sys);

        Assert.False(state.TaskDisabled);
        Assert.False(state.ServiceDisabled);

        sys.Log.Clear();
        GccTakeover.Restore(sys, state);

        // Restore must not touch what it didn't record as changed: the owner's own choice stays untouched.
        Assert.Empty(sys.Log);
        Assert.Contains("GCC", sys.DisabledTasks);
        Assert.Equal(GccServiceStartMode.Disabled, sys.ServiceStartModes["SMV4_Service"]);
    }

    [Fact]
    public void Restore_writes_back_the_original_registry_value_kind()
    {
        var sys = new FakeGccSystem();
        sys.RunValueKinds["AorusFusion"] = RegistryValueKind.ExpandString;

        var state = GccTakeover.TakeOver(sys);
        Assert.Equal(RegistryValueKind.ExpandString, state.RunValueKind);

        GccTakeover.Restore(sys, state);
        Assert.Equal(RegistryValueKind.ExpandString, sys.RunValueKinds["AorusFusion"]);
    }

    [Fact]
    public void Restore_defaults_to_string_kind_when_state_has_none_recorded()
    {
        var sys = new FakeGccSystem();
        var state = new GccTakeoverState { RunValue = @"C:\Program Files\ControlCenter\FusionStartUp.exe" };

        GccTakeover.Restore(sys, state);

        Assert.Equal(RegistryValueKind.String, sys.RunValueKinds["AorusFusion"]);
    }

    [Fact]
    public void Restore_writes_back_a_manual_service_as_manual_not_automatic()
    {
        var sys = new FakeGccSystem();
        sys.ServiceStartModes["SMV4_Service"] = GccServiceStartMode.Manual;
        sys.RunningServices.Remove("SMV4_Service"); // Manual services are typically not running

        var state = GccTakeover.TakeOver(sys);
        Assert.Equal(GccServiceStartMode.Manual, state.ServiceStartMode);

        GccTakeover.Restore(sys, state);
        Assert.Equal(GccServiceStartMode.Manual, sys.ServiceStartModes["SMV4_Service"]);
        Assert.DoesNotContain("SMV4_Service", sys.RunningServices);
    }

    [Fact]
    public void Restore_does_not_start_a_service_that_was_enabled_but_already_stopped()
    {
        var sys = new FakeGccSystem();
        sys.RunningServices.Remove("SMV4_Service"); // Automatic, but not currently running

        var state = GccTakeover.TakeOver(sys);
        Assert.True(state.ServiceDisabled);
        Assert.False(state.ServiceWasRunning);

        GccTakeover.Restore(sys, state);
        Assert.Equal(GccServiceStartMode.Automatic, sys.ServiceStartModes["SMV4_Service"]);
        Assert.DoesNotContain("SMV4_Service", sys.RunningServices);
    }

    [Fact]
    public void TakeOver_does_not_record_service_disabled_when_sc_config_fails()
    {
        var sys = new FakeGccSystem();
        sys.DisableServiceShouldFail.Add("SMV4_Service");

        var state = GccTakeover.TakeOver(sys);

        Assert.False(state.ServiceDisabled);
        Assert.Null(state.ServiceStartMode);
        Assert.Equal(GccServiceStartMode.Automatic, sys.ServiceStartModes["SMV4_Service"]); // untouched by the failed attempt
        Assert.Contains("SMV4_Service", sys.RunningServices); // still running - stop was never applied by the fake in this path

        sys.Log.Clear();
        GccTakeover.Restore(sys, state);
        Assert.DoesNotContain(sys.Log, l => l.StartsWith("enable-service"));
    }
}
