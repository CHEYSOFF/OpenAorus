using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Sensors;
using OpenAorus.Hardware.Ui;

namespace OpenAorus.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly SensorPoller _poller;
    private readonly BannerState _bannerState;

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
    public CurveEditorViewModel Curve { get; }
    public BatteryViewModel Battery { get; }
    public SettingsViewModel SettingsVm { get; }

    public MainViewModel(AppServices services)
    {
        _s = services;
        _selectedMode = _s.Settings.Mode;
        _fixedPercent = _s.Settings.FixedPercent;
        _poller = new SensorPoller(_s.Sensors, _s.Settings.PollIntervalHiddenMs);
        _poller.Updated += OnSensors;
        Curve = new CurveEditorViewModel(_s.Settings.Curve);
        Battery = new BatteryViewModel(_s, SetBanner);
        SettingsVm = new SettingsViewModel(_s);

        _bannerState = new BannerState(_s.Profile);
        SyncBanner();

        if (_s.Store.LastLoadWasReset)
        {
            // Routed through ReportOverrideNotice (not SetBanner) so this reaches the owner on every
            // model, including an unrecognised one whose banner is otherwise permanent - see BannerState.
            _bannerState.ReportOverrideNotice(BannerKind.Warning,
                "Settings could not be read and were reset to defaults; the previous file was kept as settings.json.bad. " +
                "If 'Take over from Gigabyte Control Center' was on, its record is gone - re-check it in Settings.");
            SyncBanner();
        }
        else if (_s.Store.LastLoadRepaired)
        {
            // Same route as the reset notice above, and for the same reason: the owner's saved
            // lighting changed without them asking. Only what the keyboard could not have accepted
            // was touched -- clamped where a value had a sane nearest match, removed where it did
            // not -- so this says both rather than claiming a full reset.
            _bannerState.ReportOverrideNotice(BannerKind.Warning,
                "Some saved lighting settings were out of range and have been reset to defaults. " +
                "Saved presets or per-key colours that could not be read were removed. " +
                "Everything else in your settings was kept.");
            SyncBanner();
        }
    }

    public async Task InitializeAsync()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _poller.Start();
        // Applied on every model, not just writable ones: the saved lighting is restored even
        // where the fan and charge-limit writes are withheld. On a read-only model the result
        // then always carries "read-only model", which is the expected state the model banner
        // already explains - reporting it here as a startup failure would be noise.
        var r = await _s.ApplySavedAsync();
        if (CanWrite)
        {
            StatusLine = r.Success ? $"Applied {SelectedMode} at startup" : $"Startup apply failed: {r.Error}";
            if (!r.Success) { _bannerState.ReportFailure(r.Error!); SyncBanner(); }
        }
        await Battery.RefreshAsync();
        await SettingsVm.RefreshAsync();
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
        _bannerState.ReportSensorResult(snap.Ok, snap.Error);
        SyncBanner();
    }

    private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // IsBusy also guards against a mode click landing during the delay below, and against Windows
        // firing PowerModes.Resume twice for a single wake - both would otherwise race a second write
        // sequence against the controller alongside this one.
        if (e.Mode != PowerModes.Resume || IsBusy) return;
        IsBusy = true;
        try
        {
            await Task.Delay(3000); // let the EC and WMI provider wake up
            // Same reasoning as the startup apply: a read-only model still gets its lighting
            // back after a wake, and its expected "read-only model" result is not a status line.
            var r = await _s.ApplySavedAsync();
            if (CanWrite)
                StatusLine = r.Success ? $"Re-applied {SelectedMode} after resume" : $"Resume apply failed: {r.Error}";
        }
        finally { IsBusy = false; }
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
                _bannerState.ReportSuccess();
                SyncBanner();
            }
            else
            {
                StatusLine = $"{mode} failed";
                _bannerState.ReportFailure(r.Error!);
                SyncBanner();
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
    private async Task ApplyCurveAsync()
    {
        if (!Curve.IsValid) { SetBanner(BannerKind.Warning, Curve.ValidationText); return; }
        _s.Settings.Curve = Curve.ToCurve().Points.ToList();
        await SelectModeAsync(FanMode.Custom);
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        try
        {
            var path = _s.WriteDiagnostics();
            StatusLine = $"Diagnostics saved to {path}";
            System.Windows.Clipboard.SetText(path);
        }
        catch (Exception ex) { _bannerState.ReportFailure($"Export failed: {ex.Message}"); SyncBanner(); }
    }

    /// <summary>Thin wrapper over <see cref="BannerState.ReportNotice"/> for call sites (later tasks) that
    /// want to set the banner directly - routed through <see cref="_bannerState"/> so the message sticks
    /// instead of being overwritten by the next sensor tick.</summary>
    public void SetBanner(BannerKind kind, string text)
    {
        _bannerState.ReportNotice(kind, text);
        SyncBanner();
    }

    /// <summary>Thin wrapper over <see cref="BannerState.ReportSuccess"/> for call sites that just want
    /// to clear whatever error is currently showing back to the model's baseline state.</summary>
    public void ClearBanner()
    {
        _bannerState.ReportSuccess();
        SyncBanner();
    }

    private void SyncBanner()
    {
        Banner = _bannerState.Kind;
        BannerText = _bannerState.Text;
    }

    public string TrayTooltip => Sensors.Ok
        ? $"CPU {Sensors.CpuTemp}° · GPU {Sensors.GpuTemp}° · {Sensors.Fan1Rpm}/{Sensors.Fan2Rpm} rpm · {SelectedMode}"
        : "OpenAorus - sensor read failed";
}
