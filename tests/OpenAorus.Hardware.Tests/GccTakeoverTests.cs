using OpenAorus.Hardware.Platform;

namespace OpenAorus.Hardware.Tests;

public sealed class FakeGccSystem : IGccSystem
{
    public HashSet<string> Tasks { get; } = new() { "GCC" };
    public HashSet<string> DisabledTasks { get; } = new();
    public Dictionary<string, string> RunValues { get; } = new() { ["AorusFusion"] = @"C:\Program Files\ControlCenter\FusionStartUp.exe" };
    public HashSet<string> Services { get; } = new() { "SMV4_Service" };
    public HashSet<string> DisabledServices { get; } = new();
    public HashSet<string> Running { get; } = new() { "GCC", "FusionStation" };
    public List<string> Log { get; } = new();

    public bool TaskExists(string name) => Tasks.Contains(name);
    public bool IsTaskEnabled(string name) => Tasks.Contains(name) && !DisabledTasks.Contains(name);
    public bool DisableTask(string name) { Log.Add($"disable-task {name}"); return DisabledTasks.Add(name); }
    public bool EnableTask(string name) { Log.Add($"enable-task {name}"); return DisabledTasks.Remove(name); }
    public string? ReadRunValue(string name) => RunValues.GetValueOrDefault(name);
    public void DeleteRunValue(string name) { Log.Add($"delete-run {name}"); RunValues.Remove(name); }
    public void WriteRunValue(string name, string value) { Log.Add($"write-run {name}"); RunValues[name] = value; }
    public bool ServiceExists(string name) => Services.Contains(name);
    public bool StopAndDisableService(string name) { Log.Add($"disable-service {name}"); return DisabledServices.Add(name); }
    public bool EnableService(string name) { Log.Add($"enable-service {name}"); return DisabledServices.Remove(name); }
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
        Assert.Contains("GCC", sys.DisabledTasks);
        Assert.DoesNotContain("AorusFusion", sys.RunValues.Keys);
        Assert.Contains("SMV4_Service", sys.DisabledServices);
        Assert.Empty(sys.Running);
        Assert.False(GccTakeover.IsGccActive(sys));
    }

    [Fact]
    public void TakeOver_records_only_what_existed()
    {
        var sys = new FakeGccSystem();
        sys.Tasks.Clear(); sys.RunValues.Clear(); sys.Services.Clear();
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
        Assert.Empty(sys.DisabledServices);
        Assert.Equal(new[] { "enable-task GCC", "write-run AorusFusion", "enable-service SMV4_Service" }, sys.Log);
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
        sys.DisabledTasks.Add("GCC"); sys.RunValues.Clear(); sys.DisabledServices.Add("SMV4_Service");
        Assert.True(GccTakeover.IsGccActive(sys));   // processes still running
        sys.Running.Clear();
        Assert.False(GccTakeover.IsGccActive(sys));
    }
}
