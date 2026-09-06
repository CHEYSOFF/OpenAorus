using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Ui;
using MediaColor = System.Windows.Media.Color;

namespace OpenAorus.App.ViewModels;

/// <summary>
/// The Lighting panel: the effect and its parameters, brightness, and the saved presets.
/// </summary>
/// <remarks>
/// <para>
/// The panel previews rather than stages: changing any parameter writes to the keyboard at
/// once. That is only safe because the writes are coalesced. <see cref="LightingController"/>
/// serializes reports behind a semaphore and paces them <see cref="LightingController.WriteDelayMs"/>
/// apart, so a slider bound straight to a write would queue one sequence per pixel of travel
/// and spend seconds draining values the owner has already moved past - and driving reports
/// faster than the pacing is what can wedge the keyboard's controller until it is replugged.
/// So one write is ever in flight and one value is ever waiting; a value superseded while it
/// waits is dropped without being sent.
/// </para>
/// <para>
/// Cancelling a write throws out of the controller, where every other failure comes back as a
/// failed <see cref="LightingResult"/>. Both are handled here, and only the second reaches the
/// banner: a write abandoned because the window is closing is not something to show anyone.
/// </para>
/// <para>
/// Like the other view models this one lives on the UI thread, and the drain loop's
/// continuations come back to it through the dispatcher. The latch is still locked, because
/// the tests drive it with no synchronization context at all.
/// </para>
/// </remarks>
public partial class LightingViewModel : ObservableObject
{
    private const string FailedText = "Failed";

    private readonly AppServices _s;
    private readonly Action<BannerKind, string> _banner;
    private readonly CancellationTokenSource _shutdown = new();

    private readonly object _latch = new();

    /// <summary>
    /// The whole state of the latch, in one field. Null means no drain loop is running;
    /// <see cref="Queued.Nothing"/> means one is running with nothing waiting for it; anything
    /// else is the one value waiting to be written.
    /// </summary>
    /// <remarks>
    /// One field rather than a value and a running flag, because taking the waiting value and
    /// closing the latch behind it have to be a single write. Split into two, a queuer landing
    /// between them parks a value with the latch already closed and no loop left to collect it:
    /// the panel shows the new setting and the keyboard keeps the old one, permanently. The
    /// window is tens of nanoseconds wide, so nothing observable from outside can be relied on
    /// to catch a regression - the invariant is kept by there being nothing left to split.
    /// </remarks>
    private Queued? _queued;

    private int _writers;
    private bool _suppressLiveWrite;
    private bool _lastWriteOk;
    private bool _syncingHex;

    [ObservableProperty] private LightEffect _selectedEffect;
    [ObservableProperty] private MediaColor _pickedColor;
    [ObservableProperty] private MediaColor _pickedSecondColor;
    [ObservableProperty] private string _colorHex = "";
    [ObservableProperty] private string _secondColorHex = "";
    [ObservableProperty] private int _speedPercent;
    [ObservableProperty] private int _brightnessPercent;
    [ObservableProperty] private LightDirection _direction;
    [ObservableProperty] private bool _random;
    [ObservableProperty] private string _statusText = "";

    /// <summary>Whether a write is in flight or waiting behind one; true for a whole burst.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Whether a supported lighting collection was found. False hides the panel.</summary>
    public bool KeyboardPresent => _s.KeyboardPresent;

    /// <summary>One line naming the device, or saying there is none.</summary>
    public string DeviceLine => _s.Lighting.IsPresent
        ? $"Keyboard connected · {SlotOrderName(_s.Lighting.Layout.Layout)} slot order"
        : "No supported keyboard found";

    /// <summary>Every effect the protocol defines, in id order.</summary>
    public IReadOnlyList<LightEffect> Effects { get; } = Enum.GetValues<LightEffect>();

    /// <summary>Every travel direction, for the effects that take one.</summary>
    public IReadOnlyList<LightDirection> Directions { get; } = Enum.GetValues<LightDirection>();

    /// <summary>Whether the selected effect uses <see cref="PickedColor"/>.</summary>
    public bool SupportsColor => EffectPacket.SupportsColor(SelectedEffect);

    /// <summary>Whether the selected effect uses <see cref="PickedSecondColor"/>.</summary>
    public bool SupportsSecondColor => EffectPacket.SupportsSecondColor(SelectedEffect);

    /// <summary>Whether the selected effect uses <see cref="SpeedPercent"/>.</summary>
    public bool SupportsSpeed => EffectPacket.SupportsSpeed(SelectedEffect);

    /// <summary>Whether the selected effect uses <see cref="Direction"/>.</summary>
    public bool SupportsDirection => EffectPacket.SupportsDirection(SelectedEffect);

    /// <summary>Whether the selected effect uses <see cref="Random"/>.</summary>
    public bool SupportsRandom => EffectPacket.SupportsRandom(SelectedEffect);

    /// <summary>The three shipped seeds followed by the owner's own presets.</summary>
    public ObservableCollection<LightingPreset> Presets { get; }

    /// <summary>The custom-colour editor, shown when the selected effect is <see cref="LightEffect.Custom"/>.</summary>
    public PerKeyEditorViewModel PerKey { get; }

    /// <summary>The colours the two pickers offer without typing hex.</summary>
    public IReadOnlyList<MediaColor> Swatches => ColorText.Swatches;

    /// <summary>
    /// The coalesced write loop, or an already-completed task when nothing is queued. Awaiting
    /// it waits for every value latched so far to have been written or superseded, which is what
    /// a caller has to do before anything that must not overtake a live write - applying an
    /// effect, or a per-key sequence that runs outside the latch.
    /// </summary>
    /// <remarks>
    /// Meaningful to a single-threaded caller. The field is assigned outside the latch, so a
    /// second thread queueing at the same moment could be handed the previous burst's task and
    /// return while a drain is still running; queueing happens on the UI thread, and the
    /// assignment stays outside the latch on purpose, since moving it in would run
    /// <see cref="DrainAsync"/>'s synchronous prefix - the first take and the first write -
    /// under the lock.
    /// </remarks>
    public Task LiveWrites { get; private set; } = Task.CompletedTask;

    /// <param name="services">The app's services; only the lighting and settings halves are used.</param>
    /// <param name="banner">Where a failed write is reported - <see cref="MainViewModel.SetBanner"/>.</param>
    public LightingViewModel(AppServices services, Action<BannerKind, string> banner)
    {
        _s = services;
        _banner = banner;

        var saved = _s.Settings.Lighting;
        _selectedEffect = saved.Effect;
        _pickedColor = ToMedia(saved.Color);
        _pickedSecondColor = ToMedia(saved.SecondColor);
        _speedPercent = saved.SpeedPercent;
        _brightnessPercent = saved.BrightnessPercent;
        _direction = saved.Direction;
        _random = saved.Random;
        _colorHex = ColorText.Format(_pickedColor);
        _secondColorHex = ColorText.Format(_pickedSecondColor);

        // The editor writes at the panel's live brightness rather than the saved one: the two
        // only agree once a write has landed, and the slider is what the owner is looking at.
        // It shares the shutdown token, so closing the window abandons its sequence too.
        PerKey = new PerKeyEditorViewModel(services, banner, () => BrightnessPercent, _shutdown.Token);

        // Read straight from BuiltInPresets and never cached: it returns fresh instances on
        // every access exactly so the panel can bind them to editable fields, and holding them
        // in a static would hand every view model the same three objects to be renamed in.
        // The owner's own presets are the same instances the settings hold, on purpose - an
        // edit to one of those is meant to stick, and Delete finds them by reference.
        Presets = new ObservableCollection<LightingPreset>(
            LightingSettings.BuiltInPresets.Concat(saved.Presets));
    }

    /// <summary>
    /// Cancels the write in flight and abandons anything waiting. Called when the window is
    /// closing: a paced sequence has up to three 65 ms gaps left in it, and there is nothing
    /// left to show its result on.
    /// </summary>
    public void Shutdown() => _shutdown.Cancel();

    // ---- Commands -------------------------------------------------------------------

    /// <summary>Writes the current effect now, rather than waiting for a parameter to change.</summary>
    [RelayCommand]
    private async Task ApplyEffectAsync()
    {
        if (!KeyboardPresent) return;
        QueueWrite(CurrentParameters());
        await LiveWrites;
    }

    /// <summary>Loads a preset into the panel and applies it.</summary>
    [RelayCommand]
    private async Task ApplyPresetAsync(LightingPreset? preset)
    {
        if (preset is null || !KeyboardPresent) return;

        var perKey = preset.PerKeyColors is { Count: KeyLayout.SlotCount } ? preset.PerKeyColors : null;

        // Copied in with the live write suppressed: seven property changes would otherwise queue
        // a write of a half-loaded preset. Suppression only stops new values being latched - a
        // value latched before this command started is still there, and is dealt with below.
        _suppressLiveWrite = true;
        try
        {
            // A per-key preset lands the keyboard in Custom whatever effect it was saved under,
            // so the panel says Custom too rather than showing a mode the keyboard is not in.
            SelectedEffect = perKey is null ? preset.Effect : LightEffect.Custom;
            PickedColor = ToMedia(preset.Color);
            PickedSecondColor = ToMedia(preset.SecondColor);
            SpeedPercent = preset.SpeedPercent;
            BrightnessPercent = preset.BrightnessPercent;
            Direction = preset.Direction;
            Random = preset.Random;
        }
        finally { _suppressLiveWrite = false; }

        if (perKey is not null)
        {
            // A per-key sequence runs outside the latch, so a value still waiting there - the
            // slider the owner let go of a moment before clicking - would queue on the
            // controller's gate alongside it and land last, leaving the keyboard in the effect
            // the panel has just stopped showing. It is superseded by this preset, so it is
            // dropped, and only the write already in flight is waited out.
            DropPending();
            await LiveWrites;
            await WritePerKeyAsync(perKey.ToList());
        }
        else await ApplyEffectAsync();

        if (_lastWriteOk) StatusText = $"{preset.Name} applied";
    }

    /// <summary>Saves the panel's current state as a new named preset.</summary>
    [RelayCommand]
    private void SavePreset()
    {
        var saved = _s.Settings.Lighting;
        var preset = new LightingPreset
        {
            Name = NextPresetName(),
            Effect = SelectedEffect,
            Color = ToRgb(PickedColor),
            SecondColor = ToRgb(PickedSecondColor),
            SpeedPercent = SpeedPercent,
            BrightnessPercent = BrightnessPercent,
            Direction = Direction,
            Random = Random,
            // A copy, not the settings' own list: the preset must keep the colours it was saved
            // with even after the editor paints over them.
            PerKeyColors = SelectedEffect == LightEffect.Custom && saved.PerKeyColors.Count == KeyLayout.SlotCount
                ? saved.PerKeyColors.ToList()
                : null,
        };

        saved.Presets.Add(preset);
        _s.Store.Save(_s.Settings);
        Presets.Add(preset);
        StatusText = $"Saved {preset.Name}";
    }

    /// <summary>Removes one of the owner's own presets. The three shipped seeds are not deletable.</summary>
    [RelayCommand]
    private void DeletePreset(LightingPreset? preset)
    {
        if (preset is null) return;
        if (!_s.Settings.Lighting.Presets.Remove(preset))
        {
            // Reference lookup, so this is exactly the built-ins: they are re-seeded every run
            // and deleting one would only make it come back.
            StatusText = $"{preset.Name} is built in and cannot be deleted";
            return;
        }

        _s.Store.Save(_s.Settings);
        Presets.Remove(preset);
        StatusText = $"Deleted {preset.Name}";
    }

    // ---- Live writes ----------------------------------------------------------------

    partial void OnSelectedEffectChanged(LightEffect value)
    {
        OnPropertyChanged(nameof(SupportsColor));
        OnPropertyChanged(nameof(SupportsSecondColor));
        OnPropertyChanged(nameof(SupportsSpeed));
        OnPropertyChanged(nameof(SupportsDirection));
        OnPropertyChanged(nameof(SupportsRandom));
        RequestLiveWrite();
    }

    partial void OnPickedColorChanged(MediaColor value)
    {
        Sync(() => ColorHex = ColorText.Format(value));
        RequestLiveWrite();
    }

    partial void OnPickedSecondColorChanged(MediaColor value)
    {
        Sync(() => SecondColorHex = ColorText.Format(value));
        RequestLiveWrite();
    }

    // The hex boxes and the swatch pickers are two views of one colour, so each writes the other.
    // A value that does not parse is ignored rather than corrected: the owner is mid-edit, and
    // there is nothing to send until they have typed a whole colour.
    partial void OnColorHexChanged(string value)
    {
        if (_syncingHex || !ColorText.TryParse(value, out var color)) return;
        Sync(() => PickedColor = color);
    }

    partial void OnSecondColorHexChanged(string value)
    {
        if (_syncingHex || !ColorText.TryParse(value, out var color)) return;
        Sync(() => PickedSecondColor = color);
    }

    /// <summary>Runs one half of a colour/hex sync with the other half's echo suppressed.</summary>
    private void Sync(Action update)
    {
        var wasSyncing = _syncingHex;
        _syncingHex = true;
        try { update(); }
        finally { _syncingHex = wasSyncing; }
    }
    partial void OnSpeedPercentChanged(int value) => RequestLiveWrite();
    partial void OnBrightnessPercentChanged(int value) => RequestLiveWrite();
    partial void OnDirectionChanged(LightDirection value) => RequestLiveWrite();
    partial void OnRandomChanged(bool value) => RequestLiveWrite();

    private void RequestLiveWrite()
    {
        if (_suppressLiveWrite || !KeyboardPresent) return;
        QueueWrite(CurrentParameters());
    }

    /// <summary>Latches <paramref name="parameters"/> as the next write, superseding whatever was waiting.</summary>
    private void QueueWrite(EffectParameters parameters)
    {
        lock (_latch)
        {
            var draining = _queued is not null;
            _queued = Queued.Waiting(parameters);
            if (draining) return; // the loop already running will pick this up when it comes round
        }

        BeginWrite();
        LiveWrites = DrainAsync();
    }

    /// <summary>The value waiting to be written, or null when the loop has run out of work.</summary>
    private EffectParameters? TakeNext()
    {
        lock (_latch)
        {
            var next = _queued?.Pending;
            // One assignment both takes the value and settles whether the loop goes on. There is
            // no instant in between for a QueueWrite to land in, which is why the two facts share
            // a field: see the remarks on _queued.
            _queued = next is null ? null : Queued.Nothing;
            return next;
        }
    }

    /// <summary>
    /// Drops the value waiting in the latch without disturbing the loop that would have written
    /// it, for a caller whose own write supersedes it outright.
    /// </summary>
    private void DropPending()
    {
        lock (_latch) { if (_queued is not null) _queued = Queued.Nothing; }
    }

    private bool HasPending { get { lock (_latch) return _queued?.Pending is not null; } }

    private async Task DrainAsync()
    {
        try
        {
            while (TakeNext() is { } next)
                await WriteEffectAsync(next);
        }
        catch
        {
            // A throw out of the loop must not leave the latch closed, or the panel looks alive
            // and never writes again. Opening it discards whatever was waiting, because the two
            // are one field - and that is the right half of the trade: a live loop is what the
            // panel needs back, and the value it drops is one nothing is left to write. Only on
            // this path; the normal exit already opened the latch, inside TakeNext, and doing it
            // again here could hand a drain that has since started a second one beside it.
            lock (_latch) { _queued = null; }
            throw;
        }
        finally { EndWrite(); }
    }

    /// <summary>
    /// Marks a write in progress. <see cref="IsBusy"/> covers a whole burst - a value in flight
    /// or waiting behind it - rather than one report at a time, so a continuous drag does not
    /// blink the indicator between drain iterations, and a per-key sequence running alongside a
    /// drain keeps it lit until both are done.
    /// </summary>
    private void BeginWrite()
    {
        Interlocked.Increment(ref _writers);
        IsBusy = true;
    }

    /// <summary>Ends what <see cref="BeginWrite"/> began; the last one out clears the flag.</summary>
    private void EndWrite()
    {
        if (Interlocked.Decrement(ref _writers) == 0) IsBusy = false;
    }

    /// <summary>What the latch holds while a drain loop is running: one waiting value, or none.</summary>
    private sealed class Queued
    {
        /// <summary>A running loop with nothing waiting for it.</summary>
        public static readonly Queued Nothing = new(null);

        private Queued(EffectParameters? pending) => Pending = pending;

        /// <summary>A running loop with <paramref name="parameters"/> waiting for it.</summary>
        public static Queued Waiting(EffectParameters parameters) => new(parameters);

        /// <summary>The value waiting to be written; null in <see cref="Nothing"/>.</summary>
        public EffectParameters? Pending { get; }
    }

    // One iteration of the drain loop. The burst's IsBusy is the loop's to hold, not this
    // method's: clearing it here would blink the indicator between iterations of a drag.
    private async Task WriteEffectAsync(EffectParameters parameters)
    {
        _lastWriteOk = false;
        try
        {
            var result = await _s.Lighting.ApplyEffectAsync(parameters, _shutdown.Token);
            if (!result.Success)
            {
                StatusText = FailedText;
                _banner(BannerKind.Error, result.Error!);
                return;
            }

            _lastWriteOk = true;
            StatusText = $"{parameters.Effect} applied";
            Remember(() => _s.Settings.Lighting.From(parameters));
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a failure: the owner asked for nothing and has nothing to be told.
        }
    }

    private async Task WritePerKeyAsync(List<RgbColor> colors)
    {
        _lastWriteOk = false;
        BeginWrite();
        try
        {
            var result = await _s.Lighting.ApplyPerKeyAsync(colors, BrightnessPercent, _shutdown.Token);
            if (!result.Success)
            {
                StatusText = FailedText;
                _banner(BannerKind.Error, result.Error!);
                return;
            }

            _lastWriteOk = true;
            StatusText = "Per-key colours applied";
            Remember(() =>
            {
                var saved = _s.Settings.Lighting;
                saved.From(CurrentParameters());
                saved.PerKeyColors = colors;
            });
        }
        catch (OperationCanceledException)
        {
            // The window is closing under a four-report sequence. Same as above: nothing to say.
        }
        finally { EndWrite(); }
    }

    /// <summary>
    /// Records what just landed, and writes it out only once the burst has ended: a slider drag
    /// would otherwise rewrite settings.json on every step, and every value but the last is one
    /// the owner has already moved past.
    /// </summary>
    /// <remarks>
    /// A save that cannot happen is bannered rather than thrown. Most of the callers here are
    /// fire-and-forget - nothing awaits the drain loop a slider starts - so a throw would die
    /// unobserved in that task, leaving the keyboard changed, the file not, and the status line
    /// still saying it applied; from a command it would escape instead and reach the
    /// dispatcher's unhandled handler. One failure cannot mean both.
    /// </remarks>
    private void Remember(Action update)
    {
        update();
        if (HasPending) return;
        try { _s.Store.Save(_s.Settings); }
        catch (Exception ex) { _banner(BannerKind.Error, $"Lighting could not be saved: {ex.Message}"); }
    }

    // ---- Conversions ----------------------------------------------------------------

    // WPF's colour type and the hardware's meet here and nowhere else.
    private static MediaColor ToMedia(RgbColor c) => MediaColor.FromRgb(c.R, c.G, c.B);
    private static RgbColor ToRgb(MediaColor c) => new(c.R, c.G, c.B);

    private static string SlotOrderName(KeyboardLayout layout) =>
        layout == KeyboardLayout.EngUk ? "ENG-UK" : "ENG-US";

    private EffectParameters CurrentParameters() => new(
        SelectedEffect, ToRgb(PickedColor), ToRgb(PickedSecondColor),
        SpeedPercent, BrightnessPercent, Direction, Random);

    /// <summary>First unused "Preset N". Counting the list would reuse a name after a delete.</summary>
    private string NextPresetName()
    {
        var taken = Presets.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
        {
            var name = $"Preset {n}";
            if (!taken.Contains(name)) return name;
        }
    }
}
