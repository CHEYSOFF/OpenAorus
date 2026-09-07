using System.IO;
using System.Reflection;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Diagnostics;
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

        return new AppServices
        {
            HotkeyTrace = trace,
            Hotkeys = settings.Hotkeys.Enabled ? BuildHotkeys(settings, trace) : null,
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile),
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

    /// <summary>Builds the hotkey service over the real window and the real subscription.</summary>
    /// <remarks>
    /// The mode is read from the settings rather than captured, so a press cycles from whatever
    /// the app last applied. The settings object itself is the live one, so switching hotkeys off
    /// in the Settings window takes effect on the next keypress - the only thing the
    /// <see cref="HotkeySettings.Enabled"/> test above decides is whether the channels are opened
    /// at all this run.
    /// </remarks>
    private static Hotkeys.HotkeyService BuildHotkeys(AppSettings settings, HotkeyTrace trace)
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
            () => settings.Mode,
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
