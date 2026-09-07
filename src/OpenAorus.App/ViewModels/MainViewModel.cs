using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Hotkeys;
using OpenAorus.Hardware.Sensors;
using OpenAorus.Hardware.Ui;

namespace OpenAorus.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly SensorPoller _poller;
    private readonly BannerState _bannerState;
    private readonly FanWatchdog _watchdog = new();

    /// <summary>The forced-Turbo failure the owner has already been shown, so a write that keeps
    /// failing is retried every poll without being announced again every poll.</summary>
    private string? _reportedWatchdogError;

    [ObservableProperty] private FanMode _selectedMode;
    [ObservableProperty] private int _fixedPercent;
    [ObservableProperty] private SensorSnapshot _sensors = SensorSnapshot.Empty;
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private BannerKind _banner = BannerKind.None;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private bool _isWindowVisible;
    [ObservableProperty] private AppSection _selectedSection;

    public bool CanWrite => _s.Profile.CanWrite;

    /// <summary>Whether the Lighting half of the window exists. False hides its button entirely.</summary>
    public bool LightingAvailable => Lighting.KeyboardPresent;
    public string ModelLine => $"{_s.Profile.Name} · {_s.Profile.Status} · v{_s.Version}";
    public IReadOnlyList<FanMode> Modes { get; } = Enum.GetValues<FanMode>();
    public CurveEditorViewModel Curve { get; }
    public BatteryViewModel Battery { get; }
    public LightingViewModel Lighting { get; }
    public SettingsViewModel SettingsVm { get; }

    public MainViewModel(AppServices services)
    {
        _s = services;
        _selectedMode = _s.Settings.Mode;
        _fixedPercent = _s.Settings.FixedPercent;
        _poller = new SensorPoller(_s.Sensors, _s.Settings.PollIntervalHiddenMs);
        // Dropped rather than awaited: the poller raises this on the UI thread and has nowhere to
        // await it. A poll that forces Turbo runs a write sequence lasting a few seconds, and the
        // watchdog's own latch is what stops the next tick starting a second one on top of it.
        _poller.Updated += snap => _ = OnSensorPollAsync(snap);
        // Subscribed at the controller rather than at each of the places that apply a mode -
        // startup, resume, --apply, a mode click - so a call site added later cannot forget to
        // re-arm. See FanWatchdog.NoteModeApplied for why the forced Turbo has to be let through.
        _s.Fans.Applied += OnFanModeApplied;
        Curve = new CurveEditorViewModel(_s.Settings.Curve);
        Battery = new BatteryViewModel(_s, SetBanner);
        Lighting = new LightingViewModel(_s, SetBanner);
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
            // Same route as the reset notice above, and for the same reason: settings changed
            // without the owner asking. Only what could not have been accepted was touched --
            // clamped where a value had a sane nearest match, replaced where it did not -- so
            // this says that rather than claiming a full reset, and names which half was
            // repaired: a changed fan curve is not something to report as a lighting problem.
            var parts = new List<string>();
            if (_s.Store.LastLoadLightingRepaired)
                parts.Add("Some saved lighting settings were out of range and have been reset to defaults. " +
                          "Saved presets or per-key colours that could not be read were removed.");
            if (_s.Store.LastLoadFansRepaired)
                parts.Add("Saved fan settings could have left the fans too slow to cool the machine: a Fixed duty " +
                          $"below {FanSafety.MinFixedPercent} % has been raised to it, and a custom curve that does " +
                          "not ramp up when hot has been replaced with the default curve.");
            if (_s.Store.LastLoadHotkeysRepaired)
                parts.Add("A saved hotkey overlay duration was outside the range the app accepts and has been " +
                          $"brought back inside {HotkeySettings.MinOverlaySeconds}-" +
                          $"{HotkeySettings.MaxOverlaySeconds} seconds.");
            parts.Add("Everything else in your settings was kept.");

            _bannerState.ReportOverrideNotice(BannerKind.Warning, string.Join(" ", parts));
            SyncBanner();
        }
    }

    public async Task InitializeAsync()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _poller.Start();
        if (_s.Hotkeys is { } hotkeys)
        {
            // Dropped rather than awaited, exactly as the sensor poller's tick is: the raw-input
            // hook has nowhere to await, and SelectModeAsync's own IsBusy gate is what stops two
            // sequences overlapping. Opened here rather than in AppServices.Create because --apply
            // and --dump exit before a window exists, and a registered input sink with nothing to
            // draw on would be an elevated process listening for nothing.
            hotkeys.ActionRequested += OnHotkeyRequested;
            hotkeys.Start();
            ReportSilentHotkeys(hotkeys);
        }
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

    /// <summary>
    /// Says once, at startup, that neither hotkey channel could be opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both channels write down a start failure and neither throws, so without this the feature
    /// disables itself in silence: the Fn row simply stops doing anything and the only trace is a
    /// fault line in a diagnostics dump the owner has no reason to export.
    /// </para>
    /// <para>
    /// Only when BOTH are shut. They carry different keys and fail for unrelated reasons - the WMI
    /// subscription wants a Gigabyte provider and elevation, and a chassis without one is the
    /// common case rather than a fault - so warning about half a row that still works would train
    /// the owner to read past the banner.
    /// </para>
    /// <para>
    /// Once, and from here rather than from the channels: <see cref="HotkeyService.StartError"/> is
    /// written by <see cref="HotkeyService.Start"/> and by nothing on the message path, and this is
    /// called from the one place that starts them. A channel that misbehaves per report cannot turn
    /// into a banner per report.
    /// </para>
    /// <para>
    /// Routed through <see cref="BannerState.ReportOverrideNotice"/>, for the reason the settings
    /// notices above are: the Fn keys are not a per-model feature, so an owner on an unrecognised
    /// model - whose banner is otherwise permanent - has to hear this too.
    /// </para>
    /// </remarks>
    /// <param name="hotkeys">The service whose channels were just started.</param>
    private void ReportSilentHotkeys(Hotkeys.HotkeyService hotkeys)
    {
        if (hotkeys.StartError is not { } why) return;

        _bannerState.ReportOverrideNotice(BannerKind.Warning,
            "Neither hotkey channel could be opened, so the Fn row is not being listened for and " +
            $"the fan and backlight keys will do nothing this session. {why} " +
            "Everything else in this window still works.");
        SyncBanner();
    }

    public void Shutdown()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _s.Fans.Applied -= OnFanModeApplied;
        if (_s.Hotkeys is { } hotkeys)
        {
            // Disposed here and nowhere else. The raw-input registration is process-wide and the
            // WMI watcher holds an unmanaged subscription that outlives the object if it is only
            // dropped, so both have to be given back rather than left to the finalizer.
            hotkeys.ActionRequested -= OnHotkeyRequested;
            hotkeys.Dispose();
        }
        _poller.Stop();
        // A lighting sequence is paced 65 ms per report and there is no window left to show its
        // result on, so it is dropped rather than held on to on the way out.
        Lighting.Shutdown();
    }

    /// <summary>Switches the window between Cooling and Lighting.</summary>
    [RelayCommand]
    private void SelectSection(AppSection section) => SelectedSection = section;

    /// <summary>
    /// Refuses a section that is not there. With no keyboard the Lighting button is not drawn, so
    /// this only fires on a stale binding or a later caller - and leaving the window showing an
    /// empty panel with no way back would be the worse of the two outcomes.
    /// </summary>
    partial void OnSelectedSectionChanged(AppSection value)
    {
        if (value == AppSection.Lighting && !LightingAvailable) SelectedSection = AppSection.Cooling;
    }

    partial void OnIsWindowVisibleChanged(bool value) =>
        _poller.SetInterval(value ? _s.Settings.PollIntervalVisibleMs : _s.Settings.PollIntervalHiddenMs);

    /// <summary>
    /// Handles one sensor poll: publishes the reading, reports it to the banner, and gives the
    /// thermal watchdog its look at the machine.
    /// </summary>
    /// <remarks>Public because the watchdog's behaviour is only worth anything if it can be
    /// driven a poll at a time in a test; <see cref="SensorPoller"/> is the only other caller.</remarks>
    /// <param name="snap">The reading this poll produced.</param>
    public async Task OnSensorPollAsync(SensorSnapshot snap)
    {
        Sensors = snap;
        _bannerState.ReportSensorResult(snap.Ok, snap.Error);
        SyncBanner();
        await RunWatchdogAsync(snap);
    }

    /// <summary>
    /// Forces Turbo when the CPU has reached <see cref="FanSafety.WatchdogTriggerTemperature"/> °C
    /// with the fans doing too little about it.
    /// </summary>
    /// <remarks>
    /// The CPU fan's duty is the one read, because the CPU's temperature is what triggered this;
    /// on a one-fan profile the second reading is always 0 and would fire this constantly.
    ///
    /// Nothing is reverted afterwards and nothing is written back to settings.json. Leaving the
    /// machine at full and saying so is the safe end of that choice - the owner can pick another
    /// mode the moment they see the notice - whereas dropping back out of Turbo on a timer would
    /// mean the app silently undoing the one thing it did to protect the hardware.
    /// </remarks>
    private async Task RunWatchdogAsync(SensorSnapshot snap)
    {
        // The WMI writes are withheld on an unrecognised model, so there is nothing to force and
        // no point saying so once a second on top of the banner that already explains why.
        if (!CanWrite) return;
        if (!_watchdog.Observe(snap.CpuTemp, snap.Fan1DutyPercent)) return;

        // Not gated on IsBusy: FanController serializes its own sequences, and an emergency that
        // arrives during the owner's mode click has to be the one that lands last, not the one
        // that gets dropped.
        var r = await _s.Fans.ApplyAsync(FanMode.Turbo, FixedPercent, _s.Settings.ToCurve());
        _watchdog.NoteForcedTurbo(r.Success);
        if (r.Success)
        {
            _reportedWatchdogError = null;
            SelectedMode = FanMode.Turbo;
            StatusLine = $"Fans forced to full at {snap.CpuTemp} °C";
            SetBanner(BannerKind.Warning,
                $"Fans forced to full: CPU reached {snap.CpuTemp} °C. They have been left there - " +
                "pick a fan mode yourself once the machine has cooled down.");
        }
        else
        {
            StatusLine = "Emergency fan override failed";
            // A failed write leaves the watchdog armed, so the next poll a second later tries
            // again - which is the point, and would also rewrite this banner a second later with
            // a temperature one degree different. The retries are silent while the same write
            // keeps failing in the same way; the first report stays up and says so.
            if (_reportedWatchdogError != r.Error)
            {
                _reportedWatchdogError = r.Error;
                _bannerState.ReportFailure($"CPU reached {snap.CpuTemp} °C and forcing the fans to full failed: {r.Error}");
                SyncBanner();
            }
        }
    }

    /// <summary>
    /// Re-arms the watchdog whenever the fans are put on a mode that is not its own forced Turbo.
    /// </summary>
    /// <remarks>Wired to <see cref="FanController.Applied"/> rather than to the callers, because
    /// the case that needs it most is the one furthest from here: a resume re-applies the owner's
    /// saved mode over the forced Turbo, and if the CPU never dropped below the re-arm point
    /// across the sleep the machine would come back hot, slow and unguarded.</remarks>
    private void OnFanModeApplied(FanMode mode) => _watchdog.NoteModeApplied();

    /// <summary>Raised when a serviced hotkey wants the overlay shown. The window owns the card.</summary>
    public event Action<string>? OverlayRequested;

    /// <summary>Drops the task, for the reason the sensor poller's subscription drops its own.</summary>
    private void OnHotkeyRequested(HotkeyAction action) => _ = OnHotkeyAsync(action);

    /// <summary>
    /// Acts on one hotkey. <see cref="Hotkeys.HotkeyService"/> decides; this does.
    /// </summary>
    /// <remarks>
    /// Public because the whole point of the pure decision is that the acting can be driven an
    /// action at a time in a test.
    ///
    /// Reached on the UI thread: the service hands every action to the dispatcher before raising
    /// it, because raw input arrives on the message window's thread and a WMI notice on a
    /// thread-pool callback, and everything below is a view-model property or a window.
    /// </remarks>
    /// <param name="action">What the policy decided.</param>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is null.</exception>
    public async Task OnHotkeyAsync(HotkeyAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        switch (action.Outcome)
        {
            case HotkeyOutcome.CycleFanMode when action.Mode is { } mode:
                // Through SelectModeAsync, and therefore behind IsBusy, which is the difference
                // between this and the watchdog's deliberately ungated write. The debouncer
                // throttles rather than latches - there is no key-up on these collections - so a
                // held key keeps producing fresh signals about every 250 ms while one apply is
                // five to seven steps paced at FanController.StepDelayMs. Queued, that is a
                // machine spending the next minute working through presses nobody is still
                // making; dropped, it is one mode change per press the owner can see land.
                //
                // THE CARD IS DRAWN FROM INSIDE THE APPLY, NOT AFTER IT, and only for a press the
                // gate accepted. Two things have to be true at once. A press the gate dropped -
                // an unrecognised model, or a sequence already running - changed nothing, and
                // since the saved mode only moves on success every dropped press names the same
                // target: drawn, the card would promise "Fan mode: Gaming" over and over while
                // the fans stay where they are. And a card drawn after the await is five to seven
                // paced writes late, which on this feature is the whole complaint - it is judged
                // against the volume card Windows draws on the keypress itself. So the hand-off
                // runs the moment the press is accepted and before the first write, and a write
                // that then fails is the banner's to report, exactly as a mode click's is.
                await ApplyModeAsync(mode, () => Draw(action));
                return;

            case HotkeyOutcome.SetBacklightLevel:
                Lighting.NoteFirmwareBacklight(action.Level);
                break;

            case HotkeyOutcome.Notify:
                // The firmware already did it. There is nothing to write and nothing to save.
                break;
        }

        Draw(action);
    }

    /// <summary>Asks the window for the overlay card, if the owner asked to see this signal.</summary>
    /// <remarks>The one place the card is raised, so what may draw stays a question about
    /// <see cref="HotkeyAction.ShowOverlay"/> - which only <see cref="HotkeyPolicy"/> ever sets -
    /// rather than one about where in the switch a caller happens to be.</remarks>
    /// <param name="action">The action being acted on.</param>
    private void Draw(HotkeyAction action)
    {
        if (action.ShowOverlay) OverlayRequested?.Invoke(action.Text);
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
            // Left unpinned on purpose - this is a private async void handler behind a hard-coded
            // delay, and the seam needed to reach it would cost more than the duplicate it covers.
            // MainViewModelStartupTests pins the same decision where startup makes it.
            var r = await _s.ApplySavedAsync();
            if (CanWrite)
                StatusLine = r.Success ? $"Re-applied {SelectedMode} after resume" : $"Resume apply failed: {r.Error}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>The mode buttons in the window. Nothing here needs to know what came of it.</summary>
    [RelayCommand]
    private async Task SelectModeAsync(FanMode mode) => await ApplyModeAsync(mode);

    /// <summary>
    /// Puts the fans on one mode, telling the caller at once whether the attempt was even made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two guards, and they mean different things to a caller. An unrecognised model can never be
    /// written to, and a mode already being applied means this press is one the app is deliberately
    /// dropping rather than queueing - see <see cref="OnHotkeyAsync"/>. Both come back as false
    /// before anything is attempted.
    /// </para>
    /// <para>
    /// A write that is attempted and refused is NOT one of those. It reaches the banner and the
    /// status line below and returns true: the press was taken, the machine said no, and the owner
    /// is told - which is the difference the hotkey overlay rests on.
    /// </para>
    /// <para>
    /// <paramref name="onAccepted"/> runs synchronously, after the busy latch is closed and before
    /// the first write, so a caller that wants to show something the instant a press lands does
    /// not wait out five to seven paced writes for the right to do it. A re-entrant mode change
    /// from inside it is dropped by the same latch, like any other press mid-apply.
    /// </para>
    /// </remarks>
    /// <param name="mode">The mode to apply.</param>
    /// <param name="onAccepted">Run once the change is going to be attempted; null for callers
    /// with nothing to do at that moment.</param>
    /// <returns>Whether the attempt was made - not whether it succeeded.</returns>
    private async Task<bool> ApplyModeAsync(FanMode mode, Action? onAccepted = null)
    {
        if (!CanWrite || IsBusy) return false;
        IsBusy = true;
        onAccepted?.Invoke();
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

        return true;
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
