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
    private EffectParameters? _pending;
    private bool _draining;

    private bool _suppressLiveWrite;
    private bool _lastWriteOk;

    [ObservableProperty] private LightEffect _selectedEffect;
    [ObservableProperty] private MediaColor _pickedColor;
    [ObservableProperty] private MediaColor _pickedSecondColor;
    [ObservableProperty] private int _speedPercent;
    [ObservableProperty] private int _brightnessPercent;
    [ObservableProperty] private LightDirection _direction;
    [ObservableProperty] private bool _random;
    [ObservableProperty] private string _statusText = "";
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

    /// <summary>
    /// The coalesced write loop, or an already-completed task when nothing is queued. Awaited
    /// by <see cref="ApplyEffectCommand"/>, and by the tests, which need a write started by a
    /// property change to be observable.
    /// </summary>
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
        // a write of a half-loaded preset, and for a per-key preset that write would race the
        // colour reports below through the controller's gate.
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

        if (perKey is not null) await WritePerKeyAsync(perKey.ToList());
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

    partial void OnPickedColorChanged(MediaColor value) => RequestLiveWrite();
    partial void OnPickedSecondColorChanged(MediaColor value) => RequestLiveWrite();
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
            _pending = parameters;
            if (_draining) return; // the loop already running will pick this up when it comes round
            _draining = true;
        }

        LiveWrites = DrainAsync();
    }

    private EffectParameters? TakeNext()
    {
        lock (_latch)
        {
            var next = _pending;
            _pending = null;
            // Closing the latch in the same breath as seeing it empty: a QueueWrite landing
            // between the two would otherwise park a value with no loop left to drain it.
            if (next is null) _draining = false;
            return next;
        }
    }

    private bool HasPending { get { lock (_latch) return _pending is not null; } }

    private async Task DrainAsync()
    {
        try
        {
            while (TakeNext() is { } next)
                await WriteEffectAsync(next);
        }
        catch
        {
            // A throw out of the loop - a settings file that cannot be written, say - must not
            // leave the latch closed, or the panel looks alive and never writes again. Only on
            // this path: the normal exit already opened it, inside TakeNext, and reopening it
            // here could hand a drain that has since started a second one running beside it.
            lock (_latch) { _draining = false; }
            throw;
        }
    }

    private async Task WriteEffectAsync(EffectParameters parameters)
    {
        _lastWriteOk = false;
        IsBusy = true;
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
        finally { IsBusy = false; }
    }

    private async Task WritePerKeyAsync(List<RgbColor> colors)
    {
        _lastWriteOk = false;
        IsBusy = true;
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
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Records what just landed, and writes it out only once the burst has ended: a slider drag
    /// would otherwise rewrite settings.json on every step, and every value but the last is one
    /// the owner has already moved past.
    /// </summary>
    private void Remember(Action update)
    {
        update();
        if (!HasPending) _s.Store.Save(_s.Settings);
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
