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

    /// <summary>The watchdog write failure the owner has already been shown, so a write that keeps
    /// failing is retried every poll without being announced again every poll.</summary>
    private string? _reportedWatchdogError;

    /// <summary>The two rolling means behind the temperatures the window shows.</summary>
    /// <remarks>Separate instances rather than one reading both, because the CPU and the GPU are
    /// two machines as far as the sensor is concerned: they idle and spike at different moments,
    /// and a dropped read of one must not disturb the other's number.</remarks>
    private readonly TemperatureAverage _cpuDisplayTemp = new(TemperatureAverage.DisplaySamples);
    private readonly TemperatureAverage _gpuDisplayTemp = new(TemperatureAverage.DisplaySamples);

    [ObservableProperty] private FanMode _selectedMode;
    [ObservableProperty] private int _fixedPercent;

    /// <summary>The last reading, exactly as the sensor gave it.</summary>
    /// <remarks>Raw on purpose and read by everything that must not be smoothed: the thermal
    /// watchdog's samples, the temperatures its notices quote, and the sensor-failure state the
    /// banner reports. <see cref="DisplayCpuTemp"/> and <see cref="DisplayGpuTemp"/> are the
    /// smoothed pair, and they exist only to be looked at.</remarks>
    [ObservableProperty] private SensorSnapshot _sensors = SensorSnapshot.Empty;

    /// <summary>The CPU temperature to show, in °C: a short rolling mean of the raw readings.</summary>
    /// <remarks>The raw reading moves twenty degrees between one poll and the next on this CPU, so
    /// a field bound straight to it strobes instead of reading. See
    /// <see cref="TemperatureAverage"/> for why this must never be what a decision is made on.</remarks>
    [ObservableProperty] private int _displayCpuTemp;

    /// <summary>The GPU temperature to show, in °C, smoothed the same way and separately.</summary>
    [ObservableProperty] private int _displayGpuTemp;

    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private BannerKind _banner = BannerKind.None;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeMode))]
    private bool _isBusy;

    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private bool _isWindowVisible;
    [ObservableProperty] private AppSection _selectedSection;

    public bool CanWrite => _s.Profile.CanWrite;

    /// <summary>Whether a mode press would be acted on right now.</summary>
    /// <remarks>
    /// Exactly the two conditions <see cref="ApplyModeAsync"/> drops a press on, so the mode strip
    /// can say so instead of looking pressable and doing nothing. The second of them is the one
    /// that moves: a mode change is five to seven writes paced at
    /// <see cref="FanController.StepDelayMs"/>, and for those two to three seconds the row is
    /// genuinely not taking presses. Dimming it for exactly that long is what tells the owner the
    /// app is working rather than stuck - the chip they pressed has already moved, and it settles
    /// from dim to solid when the controller agrees.
    /// </remarks>
    public bool CanChangeMode => CanWrite && !IsBusy;

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
    /// Handles one sensor poll: publishes the reading, rolls it into the two numbers on screen,
    /// reports it to the banner, and gives the thermal watchdog its look at the machine.
    /// </summary>
    /// <remarks>
    /// The reading is published raw and smoothed only for the display, and the order here is the
    /// whole of that separation: <see cref="Sensors"/> carries the sample the machine actually
    /// produced, the two averages are a side branch that nothing reads back, and
    /// <see cref="RunWatchdogAsync"/> is handed the raw snapshot. Feeding the watchdog the
    /// smoothed pair would blunt the excursions it counts while still looking like it worked,
    /// which is the one failure mode a safety guard is not allowed to have.
    ///
    /// Public because the watchdog's behaviour is only worth anything if it can be driven a poll
    /// at a time in a test; <see cref="SensorPoller"/> is the only other caller.
    /// </remarks>
    /// <param name="snap">The reading this poll produced.</param>
    public async Task OnSensorPollAsync(SensorSnapshot snap)
    {
        Sensors = snap;
        DisplayCpuTemp = _cpuDisplayTemp.Add(snap.CpuTemp);
        DisplayGpuTemp = _gpuDisplayTemp.Add(snap.GpuTemp);
        _bannerState.ReportSensorResult(snap.Ok, snap.Error);
        SyncBanner();
        await RunWatchdogAsync(snap);
    }

    /// <summary>
    /// Gives the thermal watchdog its look at one poll and carries out whatever it asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reading handed over is the raw one, never the smoothed pair the window shows. The
    /// watchdog counts consecutive polls itself and that count is only worth anything if the polls
    /// it counts are the samples the machine actually produced; feeding it an average would blunt
    /// exactly the excursions it exists to notice, and would do so invisibly.
    /// </para>
    /// <para>
    /// The CPU fan's duty is the one read, because the CPU's temperature is what decided this;
    /// on a one-fan profile the second reading is always 0 and would fire this constantly.
    /// </para>
    /// <para>
    /// Which mode goes out is <see cref="FanWatchdog"/>'s to say, and it says it three times over
    /// the life of one hot spell: the aggressive automatic curve first, then - only if a later
    /// poll's measured duty says that curve did not lift the fans - the fixed maximum, and finally
    /// the owner's own mode once the machine has been demonstrably cool for a run of polls. This
    /// method's part is to write each of them and to tell the owner which one just happened,
    /// because "the fans were turned up", "the fans are at maximum" and "you have them back" are
    /// three different pieces of news.
    /// </para>
    /// <para>
    /// The hand-back is not a timer undoing the protection. It is gated on
    /// <see cref="FanSafety.WatchdogPollsToRelease"/> consecutive polls below
    /// <see cref="FanSafety.WatchdogRearmTemperature"/> °C - three times the run it takes to
    /// engage - so by the time it fires the emergency is over by any reading. Leaving the machine
    /// pinned instead was the old answer, and it was the wrong one:
    /// <see cref="FanSafety.WatchdogLastResortMode"/> ignores the temperature entirely, so a
    /// machine that spiked once stayed at full speed at 60 °C until someone changed the mode by
    /// hand. A guard annoying enough to be switched off guards nothing.
    /// </para>
    /// </remarks>
    /// <param name="snap">The reading this poll produced, raw.</param>
    private async Task RunWatchdogAsync(SensorSnapshot snap)
    {
        // The WMI writes are withheld on an unrecognised model, so there is nothing to force, no
        // override of ours to hand back, and no point saying so once a second on top of the banner
        // that already explains why.
        if (!CanWrite) return;

        switch (_watchdog.Observe(snap.CpuTemp, snap.Fan1DutyPercent))
        {
            case WatchdogAction.Force: await ForceFansAsync(snap); break;
            case WatchdogAction.Release: await ReleaseFansAsync(snap); break;
        }
    }

    /// <summary>Writes the stage the watchdog has just moved to and says which one it was.</summary>
    /// <param name="snap">The reading that decided it, raw.</param>
    private async Task ForceFansAsync(SensorSnapshot snap)
    {
        var stage = _watchdog.Stage;
        var mode = _watchdog.ModeToApply!.Value;   // non-null on the poll that asked to force one

        // Not gated on IsBusy: FanController serializes its own sequences, and an emergency that
        // arrives during the owner's mode click has to be the one that lands last, not the one
        // that gets dropped.
        var r = await _s.Fans.ApplyAsync(mode, FixedPercent, _s.Settings.ToCurve());
        _watchdog.NoteForcedMode(r.Success);
        if (r.Success)
        {
            _reportedWatchdogError = null;
            SelectedMode = mode;
            if (stage == WatchdogStage.Raised)
            {
                StatusLine = $"Fans raised to {mode} at {snap.CpuTemp} °C";
                SetBanner(BannerKind.Warning,
                    $"Fans raised: CPU reached {snap.CpuTemp} °C, so they have been put on {mode}, " +
                    "which follows the temperature and will ease off as the machine cools. If that " +
                    "does not bring them up, they will be pinned at full speed next.");
            }
            else
            {
                StatusLine = $"Fans forced to maximum at {snap.CpuTemp} °C";
                SetBanner(BannerKind.Warning,
                    $"Fans forced to maximum: CPU is still at {snap.CpuTemp} °C and {FanSafety.WatchdogFirstStageMode} " +
                    "did not bring them up. They are at full because this mode ignores the " +
                    "temperature, and they will stay there until the CPU has held below " +
                    $"{FanSafety.WatchdogRearmTemperature} °C for a while, at which point your own " +
                    "mode goes back on. Pick a mode yourself at any time to take them back sooner.");
            }
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
                _bannerState.ReportFailure($"CPU reached {snap.CpuTemp} °C and putting the fans on {mode} failed: {r.Error}");
                SyncBanner();
            }
        }
    }

    /// <summary>
    /// Puts the mode the owner chose back on, now that the machine has been cool for long enough
    /// that the emergency is over by any reading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AppSettings.Mode"/> and not <see cref="SelectedMode"/>. The selection has been
    /// moved by the watchdog itself - that is what the two stages do to it - so reading it back
    /// here would hand the machine its own override and call it the owner's choice.
    /// <see cref="ApplyModeAsync"/> deliberately does not write the file until a mode the owner
    /// picked has actually landed, and the forced stages never go through it, so the file is still
    /// the last thing the owner asked for. Its Fixed duty and its curve come from the same place,
    /// for the same reason.
    /// </para>
    /// <para>
    /// This write re-arms the watchdog on its way out, through
    /// <see cref="FanController.Applied"/> and <see cref="OnFanModeApplied"/>, exactly as any
    /// other apply does - and that is harmless rather than the start of a second cycle.
    /// <see cref="FanWatchdog.Observe"/> has already put the stage back to
    /// <see cref="WatchdogStage.None"/> before returning <see cref="WatchdogAction.Release"/>, so
    /// the re-arm finds nothing left to undo, and a release is only ever offered from a stage - so
    /// it cannot produce another release. Getting back to a stage costs
    /// <see cref="FanSafety.WatchdogPollsToFire"/> consecutive polls at
    /// <see cref="FanSafety.WatchdogTriggerTemperature"/> °C, which is not something a machine
    /// that has just spent <see cref="FanSafety.WatchdogPollsToRelease"/> polls below
    /// <see cref="FanSafety.WatchdogRearmTemperature"/> °C is about to do.
    /// </para>
    /// <para>
    /// A failed hand-back is reported and not retried. The watchdog is already re-armed, so the
    /// machine is guarded either way; what it is not is quiet, and the owner has to be told that
    /// their mode did not go back on rather than being left to wonder why the fans are still up.
    /// </para>
    /// </remarks>
    /// <param name="snap">The reading that decided it, raw.</param>
    private async Task ReleaseFansAsync(SensorSnapshot snap)
    {
        var mode = _s.Settings.Mode;
        var r = await _s.Fans.ApplyAsync(mode, _s.Settings.FixedPercent, _s.Settings.ToCurve());
        if (r.Success)
        {
            _reportedWatchdogError = null;
            SelectedMode = mode;
            StatusLine = $"Fans handed back to {mode} at {snap.CpuTemp} °C";
            SetBanner(BannerKind.Info,
                $"Fans handed back: the CPU has stayed below {FanSafety.WatchdogRearmTemperature} °C " +
                $"for {FanSafety.WatchdogPollsToRelease} readings in a row, so your own {mode} setting " +
                "is back on. The fans were only taken off you while the machine was genuinely hot.");
        }
        else
        {
            StatusLine = "Handing the fans back failed";
            _reportedWatchdogError = r.Error;
            _bannerState.ReportFailure(
                $"The machine has cooled, but putting the fans back on {mode} failed: {r.Error}. " +
                "They are still on the mode the app forced - pick one yourself to take them back.");
            SyncBanner();
        }
    }

    /// <summary>
    /// Re-arms the watchdog whenever the fans are put on a mode that is not one of its own stages.
    /// </summary>
    /// <remarks>Wired to <see cref="FanController.Applied"/> rather than to the callers, because
    /// the case that needs it most is the one furthest from here: a resume re-applies the owner's
    /// saved mode over whatever the watchdog forced, and if the CPU never dropped below the
    /// re-arm point across the sleep the machine would come back hot, slow and unguarded. The
    /// watchdog's own two applies come through here too and are the ones it ignores - see
    /// <see cref="FanWatchdog.NoteModeApplied"/>.</remarks>
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

            case HotkeyOutcome.StepPanelBrightness:
            {
                // THE ONLY HOTKEY WHOSE CARD IS WORDED HERE RATHER THAN IN THE POLICY, because it
                // is the only one whose result nobody can predict: which level a step lands on is
                // the panel's to say, and a card built from the request would show a number the
                // screen is not showing. Null means nothing moved - no controllable panel, or a
                // write the panel refused - and nothing moved is nothing to narrate.
                //
                // Off the UI thread, unlike every other arm. This one makes a WMI round trip, and
                // it is reached from a raw-input callback by way of the dispatcher; run inline it
                // would stall the message pump for as long as root\WMI takes to answer.
                var level = await Task.Run(() => _s.PanelBrightness.Step(action.Step));
                if (level is { } percent) Draw(action with { Text = $"Display brightness {percent} %" });
                return;
            }

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
    /// <para>
    /// THE SELECTION MOVES AT THAT SAME MOMENT, not when the sequence finishes. Five to seven
    /// writes paced at <see cref="FanController.StepDelayMs"/> is two to three seconds, and a
    /// custom curve with many points is longer; leaving the old mode lit for all of it made the
    /// window look frozen rather than busy. So the press is acknowledged when it is accepted, the
    /// row dims for as long as the writes take - see <see cref="CanChangeMode"/> - and a refused
    /// write puts the selection back where it was, because a window claiming a mode the machine
    /// is not in would be a worse lie than the frozen one this replaced.
    /// </para>
    /// <para>
    /// <c>settings.Mode</c> is deliberately not moved early with it. The window may run ahead of
    /// the machine; the file may not, because it is what goes back on at the next start and at the
    /// next resume, and that has to be a mode the controller actually took.
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

        var previous = SelectedMode;
        SelectedMode = mode;
        StatusLine = $"Applying {mode}…";
        onAccepted?.Invoke();
        try
        {
            var r = await _s.Fans.ApplyAsync(mode, FixedPercent, _s.Settings.ToCurve());
            if (r.Success)
            {
                _s.Settings.Mode = mode;
                _s.Store.Save(_s.Settings);
                StatusLine = $"{mode} applied";
                _bannerState.ReportSuccess();
                SyncBanner();
            }
            else
            {
                SelectedMode = previous;
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

    /// <summary>The tray hover text. Reads the smoothed pair, as the window does: it is the same
    /// two numbers in a smaller place, and a tooltip disagreeing with the window it belongs to
    /// would just look broken.</summary>
    public string TrayTooltip => Sensors.Ok
        ? $"CPU {DisplayCpuTemp}° · GPU {DisplayGpuTemp}° · {Sensors.Fan1Rpm}/{Sensors.Fan2Rpm} rpm · {SelectedMode}"
        : "OpenAorus - sensor read failed";
}
