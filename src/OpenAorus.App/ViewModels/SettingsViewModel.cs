using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.Platform;

namespace OpenAorus.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _s;

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _gccTakenOver;
    [ObservableProperty] private bool _gccActive;
    [ObservableProperty] private int _pollVisibleMs;
    [ObservableProperty] private int _pollHiddenMs;
    [ObservableProperty] private string _message = "";

    public bool CanWrite => _s.Profile.CanWrite;

    public string SettingsPath => _s.Store.Path;

    /// <summary>The Fn hotkey card, which is the one part of this dialog outside
    /// <see cref="CanWrite"/> - see <see cref="HotkeysViewModel"/> for why.</summary>
    public HotkeysViewModel Hotkeys { get; }

    public SettingsViewModel(AppServices s)
    {
        _s = s;
        Hotkeys = new HotkeysViewModel(s, text => Message = text);
        _pollVisibleMs = s.Settings.PollIntervalVisibleMs;
        _pollHiddenMs = s.Settings.PollIntervalHiddenMs;
        _gccTakenOver = s.Settings.Takeover is not null;
    }

    public async Task RefreshAsync()
    {
        StartWithWindows = await Task.Run(StartupTask.IsEnabled);
        GccActive = await Task.Run(() => GccTakeover.IsGccActive(_s.Gcc));
        _s.Settings.StartWithWindows = StartWithWindows;
        if (GccTakenOver && GccActive)
            Message = "Gigabyte Control Center is running again (an update may have re-enabled it). Toggle takeover off and on to re-park it.";
    }

    [RelayCommand]
    private async Task ToggleStartupAsync()
    {
        var enabling = !StartWithWindows;
        if (enabling && !StartupTask.IsPublishedExe(_s.ExePath))
        {
            Message = "Start with Windows needs the published OpenAorus.exe - it will not work when launched via `dotnet run`, which starts dotnet.exe instead.";
            return;
        }

        var ok = await Task.Run(() => StartWithWindows ? StartupTask.Disable() : StartupTask.Enable(_s.ExePath));
        if (!ok) { Message = "schtasks failed - are you elevated?"; return; }
        StartWithWindows = !StartWithWindows;
        _s.Settings.StartWithWindows = StartWithWindows;
        _s.Store.Save(_s.Settings);
        Message = StartWithWindows ? "Logon task 'OpenAorus' created (highest privileges, no UAC prompt)." : "Logon task removed.";
    }

    [RelayCommand]
    private async Task ToggleTakeoverAsync()
    {
        if (GccTakenOver)
        {
            var state = _s.Settings.Takeover;
            if (state is not null) await Task.Run(() => GccTakeover.Restore(_s.Gcc, state));
            _s.Settings.Takeover = null;
            GccTakenOver = false;
            Message = "Gigabyte Control Center autostart restored. It will run again after the next logon.";
        }
        else
        {
            _s.Settings.Takeover = await Task.Run(() => GccTakeover.TakeOver(_s.Gcc));
            GccTakenOver = true;
            Message = "GCC task, autostart entry and service disabled; running GCC processes closed.";
        }
        _s.Store.Save(_s.Settings);
        await RefreshAsync();
    }

    [RelayCommand]
    private void SaveIntervals()
    {
        _s.Settings.PollIntervalVisibleMs = Math.Clamp(PollVisibleMs, 250, 10000);
        _s.Settings.PollIntervalHiddenMs = Math.Clamp(PollHiddenMs, 1000, 60000);
        PollVisibleMs = _s.Settings.PollIntervalVisibleMs;
        PollHiddenMs = _s.Settings.PollIntervalHiddenMs;
        _s.Store.Save(_s.Settings);
        Message = "Poll intervals saved (take effect on next show/hide).";
    }
}
