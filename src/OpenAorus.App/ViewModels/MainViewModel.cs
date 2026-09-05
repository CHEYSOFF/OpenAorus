using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;

namespace OpenAorus.App.ViewModels;

public enum BannerKind { None, Info, Warning, Error }

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly SensorPoller _poller;

    [ObservableProperty] private FanMode _selectedMode;
    [ObservableProperty] private int _fixedPercent;
    [ObservableProperty] private SensorSnapshot _sensors = SensorSnapshot.Empty;
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private BannerKind _banner = BannerKind.None;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private bool _isWindowVisible;

    public bool CanWrite => _s.Profile.CanWrite;
    public string ModelLine => $"{_s.Profile.Name} · {_s.Profile.Status} · v{_s.Version}";
    public IReadOnlyList<FanMode> Modes { get; } = Enum.GetValues<FanMode>();

    public MainViewModel(AppServices services)
    {
        _s = services;
        _selectedMode = _s.Settings.Mode;
        _fixedPercent = _s.Settings.FixedPercent;
        _poller = new SensorPoller(_s.Sensors, _s.Settings.PollIntervalHiddenMs);
        _poller.Updated += OnSensors;

        Banner = _s.Profile.Status switch
        {
            ProfileStatus.Unknown => BannerKind.Error,
            ProfileStatus.Untested => BannerKind.Warning,
            _ => BannerKind.None,
        };
        BannerText = _s.Profile.Status switch
        {
            ProfileStatus.Unknown => $"'{_s.Profile.Name}' is not a recognised Gigabyte laptop. Read-only mode. Export diagnostics and open an issue.",
            ProfileStatus.Untested => "Untested model - compare fan duty read-back with Gigabyte Control Center before trusting it.",
            _ => "",
        };
    }

    public async Task InitializeAsync()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _poller.Start();
        if (CanWrite)
        {
            var r = await _s.ApplySavedAsync();
            StatusLine = r.Success ? $"Applied {SelectedMode} at startup" : $"Startup apply failed: {r.Error}";
            if (!r.Success) SetBanner(BannerKind.Error, r.Error!);
        }
    }

    public void Shutdown()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _poller.Stop();
    }

    partial void OnIsWindowVisibleChanged(bool value) =>
        _poller.SetInterval(value ? _s.Settings.PollIntervalVisibleMs : _s.Settings.PollIntervalHiddenMs);

    private void OnSensors(SensorSnapshot snap)
    {
        Sensors = snap;
        if (!snap.Ok && Banner == BannerKind.None) SetBanner(BannerKind.Error, snap.Error ?? "sensor read failed");
        else if (snap.Ok && Banner == BannerKind.Error && _s.Profile.Status != ProfileStatus.Unknown) ClearBanner();
    }

    private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || !CanWrite) return;
        await Task.Delay(3000); // let the EC and WMI provider wake up
        var r = await _s.ApplySavedAsync();
        StatusLine = r.Success ? $"Re-applied {SelectedMode} after resume" : $"Resume apply failed: {r.Error}";
    }

    [RelayCommand]
    private async Task SelectModeAsync(FanMode mode)
    {
        if (!CanWrite || IsBusy) return;
        IsBusy = true;
        try
        {
            var r = await _s.Fans.ApplyAsync(mode, FixedPercent, _s.Settings.ToCurve());
            if (r.Success)
            {
                SelectedMode = mode;
                _s.Settings.Mode = mode;
                _s.Store.Save(_s.Settings);
                StatusLine = $"{mode} applied";
                if (Banner == BannerKind.Error) ClearBanner();
            }
            else
            {
                StatusLine = $"{mode} failed";
                SetBanner(BannerKind.Error, r.Error!);
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyFixedAsync()
    {
        _s.Settings.FixedPercent = FixedPercent;
        await SelectModeAsync(FanMode.Fixed);
    }

    [RelayCommand]
    private async Task ApplyCurveAsync() => await SelectModeAsync(FanMode.Custom);

    [RelayCommand]
    private void ExportDiagnostics()
    {
        try
        {
            var path = _s.WriteDiagnostics();
            StatusLine = $"Diagnostics saved to {path}";
            System.Windows.Clipboard.SetText(path);
        }
        catch (Exception ex) { SetBanner(BannerKind.Error, $"Export failed: {ex.Message}"); }
    }

    public void SetBanner(BannerKind kind, string text) { Banner = kind; BannerText = text; }

    public void ClearBanner()
    {
        Banner = _s.Profile.Status == ProfileStatus.Untested ? BannerKind.Warning : BannerKind.None;
        BannerText = Banner == BannerKind.Warning ? "Untested model - compare fan duty read-back with Gigabyte Control Center before trusting it." : "";
    }

    public string TrayTooltip => Sensors.Ok
        ? $"CPU {Sensors.CpuTemp}° · GPU {Sensors.GpuTemp}° · {Sensors.Fan1Rpm}/{Sensors.Fan2Rpm} rpm · {SelectedMode}"
        : "OpenAorus - sensor read failed";
}
