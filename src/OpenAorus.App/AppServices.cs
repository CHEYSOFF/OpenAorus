using System.IO;
using System.Reflection;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Diagnostics;
using OpenAorus.Hardware.Display;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Hotkeys;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;
using OpenAorus.Hardware.Platform;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.App;

public sealed class AppServices
{
    public required ModelProfile Profile { get; init; }
    public required IGigabyteWmi Wmi { get; init; }
    public required FanController Fans { get; init; }
    public required SensorReader Sensors { get; init; }
    public required BatteryController Battery { get; init; }
    public required SettingsStore Store { get; init; }
    public required AppSettings Settings { get; init; }
    public required IGccSystem Gcc { get; init; }
    public required LightingController Lighting { get; init; }

    /// <summary>Whether a supported lighting collection was found. False hides the lighting UI
    /// and makes every lighting call a no-op; nothing else about the app changes.</summary>
    public required bool KeyboardPresent { get; init; }
    public required string Version { get; init; }
    public required string ExePath { get; init; }

    /// <summary>The display panel's own brightness, for the two Fn keys nothing else services.</summary>
    /// <remarks>
    /// <para>
    /// NOT REACHED THROUGH <see cref="Wmi"/>, and that is the one hardware path in this app which
    /// is not. The Gigabyte interface answers <c>Invalid object</c> for brightness on this model -
    /// the embedded controller does not expose the panel - so this goes through Windows' own
    /// <c>WmiMonitorBrightness</c> classes instead. See <see cref="IPanelBrightness"/>.
    /// </para>
    /// <para>
    /// Defaulted rather than required, and to a panel that does nothing rather than to null: every
    /// test rig that has no interest in the screen gets a controller that is safe to call, and no
    /// call site on a callback path has to remember a null check.
    /// </para>
    /// </remarks>
    public PanelBrightnessController PanelBrightness { get; init; } = new(new NoPanelBrightness());

    /// <summary>What the two hotkey channels have seen, shared by whatever opens them and read
    /// back by <see cref="WriteDiagnostics"/>.</summary>
    /// <remarks>Owned here rather than by the channels so a dump exported after the fact still
    /// carries them: "nothing has arrived" is the answer that separates a report this app misread
    /// from a chassis that never sent one, and neither is visible any other way.</remarks>
    public HotkeyTrace HotkeyTrace { get; init; } = new();

    /// <summary>The Fn hotkey channels. Null when hotkeys are switched off in settings.</summary>
    /// <remarks>Built here but not opened here: <see cref="Hotkeys.HotkeyService.Start"/> is
    /// called from <see cref="ViewModels.MainViewModel.InitializeAsync"/>, so <c>--apply</c> and
    /// <c>--dump</c> - which exit before a window exists - never register an input sink. Nothing
    /// in the constructors touches the OS.</remarks>
    public Hotkeys.HotkeyService? Hotkeys { get; init; }

    public static AppServices Create()
    {
        var profile = ModelProfile.Detect(SystemInfo.GetProductName());
        var wmi = new GigabyteWmi();
        var store = new SettingsStore(SettingsStore.DefaultPath);
        // Loaded once and shared: a second store.Load() here would hand the app a divergent copy
        // of the settings, and every save from one would quietly undo the other.
        var settings = store.Load();
        var keyboard = KeyboardHid.Open();
        var layout = settings.Lighting.LayoutOverride is { } forced
            ? KeyLayout.For(forced)
            : KeyLayout.ForProduct(keyboard.Identity?.Pid ?? 0);

        // One trace, handed to both channels and read back by the dump. Two would mean a dump
        // that told half the story about a keyboard that is only half working.
        var trace = new HotkeyTrace();

        // Built here rather than in the initializer because the hotkey cursor has to be hung off
        // this exact controller - the one every mode change in the app goes through.
        var fans = new FanController(wmi, profile);

        return new AppServices
        {
            // Nothing is read here: the class enumerates root\WMI on first use, and a machine with
            // no controllable panel simply answers null there. Constructing it is free, so --apply
            // and --dump, which exit before a window exists, never touch the panel.
            PanelBrightness = new PanelBrightnessController(new WmiPanelBrightness()),
            HotkeyTrace = trace,
            Hotkeys = settings.Hotkeys.Enabled
                ? BuildHotkeys(settings, trace, TrackAppliedMode(fans, settings.Mode))
                : null,
            Profile = profile,
            Wmi = wmi,
            Fans = fans,
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = store,
            Settings = settings,
            Gcc = new WindowsGccSystem(),
            Lighting = new LightingController(keyboard, layout),
            KeyboardPresent = keyboard.IsPresent,
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
            // Not Assembly.Location: it is empty in a single-file build, which is exactly what
            // CI publishes, and this path is what the logon task is registered against.
            ExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenAorus.exe"),
        };
    }

    /// <summary>
    /// Follows the mode the fans are actually running, starting from the saved one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT <c>settings.Mode</c>, and the difference is a safety one. The thermal watchdog forces
    /// Turbo without writing it to settings.json - deliberately, so a forced Turbo is not what the
    /// machine boots into next time - so the saved mode still names whatever the owner last chose
    /// while the fans are at full, and the two diverge exactly when the machine is hottest.
    /// </para>
    /// <para>
    /// A fan key cycling from the saved mode there takes a CPU at
    /// <see cref="FanSafety.WatchdogTriggerTemperature"/> °C <em>down</em> from full - one press
    /// from a saved Quiet applies Normal - and that apply re-arms the watchdog through
    /// <see cref="FanController.Applied"/>, so the next poll forces Turbo again and the machine
    /// flaps. From a saved Turbo the first press lands on Quiet at 90 °C.
    /// </para>
    /// <para>
    /// Subscribed at the controller rather than at the callers, for the reason
    /// <c>MainViewModel</c> subscribes the watchdog there: it covers the startup apply, the resume
    /// apply, <c>--apply</c>, a mode click, the forced Turbo and the hotkey itself, and a call
    /// site added later cannot forget it. <see cref="FanController.Applied"/> is raised only for a
    /// sequence that succeeded, so a refused write leaves the cursor where the machine is.
    /// </para>
    /// </remarks>
    /// <param name="fans">The controller every mode change goes through.</param>
    /// <param name="saved">The mode the app starts believing in, before anything is applied.</param>
    /// <returns>A reader of the last applied mode.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fans"/> is null.</exception>
    internal static Func<FanMode> TrackAppliedMode(FanController fans, FanMode saved)
    {
        ArgumentNullException.ThrowIfNull(fans);

        var applied = saved;
        fans.Applied += mode => applied = mode;
        return () => applied;
    }

    /// <summary>Builds the hotkey service over the real window and the real subscription.</summary>
    /// <remarks>
    /// The mode is read through <paramref name="currentMode"/> rather than captured, so a press
    /// cycles from whatever the app last applied - including a Turbo the watchdog forced and never
    /// saved; see <see cref="TrackAppliedMode"/> for why that is not the same as the saved mode.
    /// The settings object itself is the live one, so switching hotkeys off in the Settings window
    /// takes effect on the next keypress - the only thing the <see cref="HotkeySettings.Enabled"/>
    /// test above decides is whether the channels are opened at all this run.
    /// </remarks>
    /// <param name="settings">The owner's live settings.</param>
    /// <param name="trace">The shared record of what the channels deliver.</param>
    /// <param name="currentMode">What the fans are actually running.</param>
    private static Hotkeys.HotkeyService BuildHotkeys(
        AppSettings settings, HotkeyTrace trace, Func<FanMode> currentMode)
    {
        // Captured on the thread Create runs on, which is the one OnStartup runs on and the one
        // the window will live on. Both channels deliver on some other thread - the message
        // window's for raw input, a thread-pool callback for WMI - and every action ends at a
        // view-model property or the overlay card, so this is where they have to come back to.
        var ui = System.Windows.Application.Current?.Dispatcher
                 ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

        return new Hotkeys.HotkeyService(
            new Hotkeys.RawInputWindow(trace),
            new Hotkeys.WmiEventListener(trace),
            settings.Hotkeys,
            currentMode,
            post: work =>
            {
                // A key pressed while the app is closing has nowhere to land, and a dispatcher
                // that has begun shutting down refuses the work by throwing - on a callback
                // thread, where the throw would be recorded as a channel fault for no reason.
                if (ui.HasShutdownStarted || ui.HasShutdownFinished) return;
                try { ui.InvokeAsync(work); }
                catch (TaskCanceledException) { }
            });
    }

    /// <summary>Re-applies the persisted fan mode, charge limit and lighting (startup, resume, --apply).
    /// Attempts all three even if one fails, so a fan-write failure never strands the saved charge limit
    /// or the saved lighting unrestored. A machine with no keyboard simply skips the lighting step.
    /// A model the profile table does not recognise skips only the two WMI halves.</summary>
    public async Task<WmiResult> ApplySavedAsync()
    {
        var errors = new List<string>();

        if (Profile.CanWrite)
        {
            var fans = await Fans.ApplyAsync(Settings.Mode, Settings.FixedPercent, Settings.ToCurve());
            var battery = Battery.SetLimit(Settings.ChargeLimitEnabled, Settings.ChargeStopPercent);
            if (!fans.Success) errors.Add($"fan mode: {fans.Error}");
            if (!battery.Success) errors.Add($"charge limit: {battery.Error}");
        }
        else
        {
            // Only the WMI writes are withheld on an unrecognised model: the duty scale is a
            // per-model guess there, and driving the fans off a wrong one is the risk the
            // read-only rule exists for. Lighting is HID - no elevation, no model profile,
            // nothing model-specific to get wrong - and unrecognised models are the common
            // case, so gating it here would leave most owners with no lighting restore at all.
            errors.Add("read-only model");
        }

        if (KeyboardPresent)
        {
            var saved = Settings.Lighting;
            // Custom is the only effect whose colours live outside the effect report, and it is
            // only restorable if a full slot's worth of them was saved. Anything else - including
            // a colour list SettingsStore had to discard - falls back to selecting the effect,
            // which leaves the keyboard showing the colours it already holds.
            var lighting = saved.Effect == LightEffect.Custom && saved.PerKeyColors.Count == KeyLayout.SlotCount
                ? await Lighting.ApplyPerKeyAsync(saved.PerKeyColors, saved.BrightnessPercent)
                : await Lighting.ApplyEffectAsync(saved.ToParameters());
            if (!lighting.Success) errors.Add($"lighting: {lighting.Error}");
        }

        return errors.Count == 0 ? WmiResult.Ok() : WmiResult.Fail(string.Join("; ", errors));
    }

    public string WriteDiagnostics()
    {
        var text = DiagnosticsDump.Render(Wmi, Profile, Version, HotkeyTrace);
        var dir = Path.GetDirectoryName(Store.Path)!;
        Directory.CreateDirectory(dir);
        var safe = string.Concat(Profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Replace(' ', '-');
        var path = Path.Combine(dir, $"diagnostics-{safe}.txt");
        File.WriteAllText(path, text);
        return path;
    }
}
