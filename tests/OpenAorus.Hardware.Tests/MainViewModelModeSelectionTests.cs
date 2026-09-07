using System.IO;
using OpenAorus.App;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// What the window shows between the press and the machine agreeing with it.
/// </summary>
/// <remarks>
/// <para>
/// Applying a mode is five to seven WMI writes paced at <see cref="FanController.StepDelayMs"/>,
/// so the press the owner just made takes two to three seconds to land - and a custom curve with
/// many points takes considerably longer. The selection used to move only once all of that had
/// succeeded, which left the old mode lit for the whole sequence: not a window that looks busy,
/// a window that looks broken.
/// </para>
/// <para>
/// The tests below drive that gap directly. The controller's inter-step delay is a
/// <see cref="TaskCompletionSource"/> rather than a clock, so the sequence can be held open and
/// the window inspected in the middle of it without waiting on real time or racing it.
/// </para>
/// </remarks>
public class MainViewModelModeSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private (MainViewModel vm, FakeGigabyteWmi wmi, AppSettings settings) Make(
        Func<int, Task>? delay = null, FanMode saved = FanMode.Normal)
    {
        var p = ModelProfile.Detect("AORUS 17G KD");
        var wmi = new FakeGigabyteWmi();
        var settings = new AppSettings { Mode = saved };
        var services = new AppServices
        {
            Profile = p,
            Wmi = wmi,
            Fans = new FanController(wmi, p, delay: delay ?? (_ => Task.CompletedTask)),
            Sensors = new SensorReader(wmi, p),
            Battery = new BatteryController(wmi),
            Store = new SettingsStore(Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json")),
            Settings = settings,
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(new FakeKeyboardHid(), KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = false,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        };
        return (new MainViewModel(services), wmi, settings);
    }

    [Fact]
    public async Task The_selection_moves_before_the_writes_finish()
    {
        var held = new TaskCompletionSource();
        var (vm, wmi, _) = Make(delay: _ => held.Task);

        var apply = vm.SelectModeCommand.ExecuteAsync(FanMode.Gaming);

        // Held on the first inter-step delay, so the sequence cannot have finished - and the
        // window has already moved. This is the whole change: the press is acknowledged when it
        // is accepted, not two to three seconds later when the controller agrees.
        Assert.Equal(FanMode.Gaming, vm.SelectedMode);
        Assert.True(wmi.Calls.Count < 5);
        Assert.False(apply.IsCompleted);

        held.SetResult();
        await apply;

        Assert.Equal(FanMode.Gaming, vm.SelectedMode);
    }

    [Fact]
    public async Task Work_in_progress_is_visible_for_the_whole_sequence()
    {
        var held = new TaskCompletionSource();
        var (vm, _, _) = Make(delay: _ => held.Task);

        var apply = vm.SelectModeCommand.ExecuteAsync(FanMode.Gaming);

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanChangeMode);
        Assert.Contains("Applying", vm.StatusLine);
        Assert.Contains("Gaming", vm.StatusLine);

        held.SetResult();
        await apply;

        // And it stops saying so the moment it is done, rather than leaving a stale "Applying".
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanChangeMode);
        Assert.Equal("Gaming applied", vm.StatusLine);
    }

    [Fact]
    public async Task A_failed_write_puts_the_selection_back()
    {
        var (vm, wmi, settings) = Make(saved: FanMode.Quiet);
        var before = vm.SelectedMode;
        wmi.FailOn.Add("SetAutoFanStatus");

        await vm.SelectModeCommand.ExecuteAsync(FanMode.Gaming);

        // A refused write must not leave the window claiming a mode the machine is not in: that
        // would be a worse lie than the frozen selection this replaced.
        Assert.Equal(before, vm.SelectedMode);
        Assert.Equal("Gaming failed", vm.StatusLine);
        Assert.Equal(FanMode.Quiet, settings.Mode);
    }

    [Fact]
    public async Task A_failed_write_puts_the_selection_back_where_the_owner_left_it_not_where_it_started()
    {
        var (vm, wmi, _) = Make(saved: FanMode.Quiet);
        await vm.SelectModeCommand.ExecuteAsync(FanMode.Turbo);
        wmi.FailOn.Add("SetAutoFanStatus");   // in Gaming's sequence, and only refused from here on

        await vm.SelectModeCommand.ExecuteAsync(FanMode.Gaming);

        Assert.Equal(FanMode.Turbo, vm.SelectedMode);
    }

    [Fact]
    public async Task A_press_dropped_while_a_sequence_is_running_does_not_move_the_selection()
    {
        var held = new TaskCompletionSource();
        var (vm, _, _) = Make(delay: _ => held.Task);

        var apply = vm.SelectModeCommand.ExecuteAsync(FanMode.Gaming);
        await vm.SelectModeCommand.ExecuteAsync(FanMode.Turbo);   // dropped by the busy latch

        // Dropped means dropped: nothing was attempted, so nothing may be shown as attempted.
        Assert.Equal(FanMode.Gaming, vm.SelectedMode);

        held.SetResult();
        await apply;

        Assert.Equal(FanMode.Gaming, vm.SelectedMode);
    }

    [Fact]
    public async Task A_press_on_a_model_that_cannot_be_written_to_moves_nothing()
    {
        var p = ModelProfile.Detect("Some Other Laptop");
        var wmi = new FakeGigabyteWmi();
        var vm = new MainViewModel(new AppServices
        {
            Profile = p,
            Wmi = wmi,
            Fans = new FanController(wmi, p, delay: _ => Task.CompletedTask),
            Sensors = new SensorReader(wmi, p),
            Battery = new BatteryController(wmi),
            Store = new SettingsStore(Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json")),
            Settings = new AppSettings { Mode = FanMode.Quiet },
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(new FakeKeyboardHid(), KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = false,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
        });

        await vm.SelectModeCommand.ExecuteAsync(FanMode.Turbo);

        Assert.Equal(FanMode.Quiet, vm.SelectedMode);
        Assert.False(vm.CanChangeMode);
    }

    [Fact]
    public async Task The_saved_mode_still_only_moves_once_the_write_has_landed()
    {
        var held = new TaskCompletionSource();
        var (vm, _, settings) = Make(delay: _ => held.Task, saved: FanMode.Quiet);

        var apply = vm.SelectModeCommand.ExecuteAsync(FanMode.Gaming);

        // The window is allowed to run ahead of the machine; settings.json is not. What is on
        // disk is what the app puts back at the next start, and that must be a mode the
        // controller actually took.
        Assert.Equal(FanMode.Quiet, settings.Mode);

        held.SetResult();
        await apply;

        Assert.Equal(FanMode.Gaming, settings.Mode);
    }

    [Fact]
    public async Task The_fixed_slider_moves_the_selection_the_moment_it_is_released_too()
    {
        var held = new TaskCompletionSource();
        var (vm, _, _) = Make(delay: _ => held.Task, saved: FanMode.Quiet);
        vm.FixedPercent = 55;

        var apply = vm.ApplyFixedCommand.ExecuteAsync(null);

        Assert.Equal(FanMode.Fixed, vm.SelectedMode);

        held.SetResult();
        await apply;

        Assert.Equal(FanMode.Fixed, vm.SelectedMode);
    }
}
