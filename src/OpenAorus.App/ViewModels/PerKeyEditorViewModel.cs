using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Ui;
using MediaColor = System.Windows.Media.Color;

namespace OpenAorus.App.ViewModels;

/// <summary>One key in the editor: where it lives on the wire, what it is called, and its colour.</summary>
public partial class KeySlotViewModel : ObservableObject
{
    [ObservableProperty] private MediaColor _color;

    /// <summary>Whether this is the key the owner painted last. Only one key is ever marked.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>The lighting slot, 0..127. This, not the key's position on screen, is what is sent.</summary>
    public int Slot { get; }

    /// <summary>The name the layout gives this slot.</summary>
    public string Name { get; }

    /// <summary>The short label drawn on the key; <see cref="Name"/> rarely fits.</summary>
    public string Caption { get; }

    /// <summary>
    /// Drawn width in pixels, with the gap between keys already taken out, so that a row of
    /// 1-unit keys and a 2-unit key measure the same as three units of the row above.
    /// </summary>
    public double Width { get; }

    /// <summary>What the key says on hover - the full name and the slot it will light.</summary>
    public string Tooltip => $"{Name} · slot {Slot}";

    /// <param name="slot">The lighting slot this key occupies.</param>
    /// <param name="name">The layout's name for that slot.</param>
    /// <param name="units">Drawn width in key-widths.</param>
    /// <param name="color">The colour to start from.</param>
    public KeySlotViewModel(int slot, string name, double units, MediaColor color)
    {
        Slot = slot;
        Name = name;
        Caption = KeyboardGeometry.CaptionFor(name);
        Width = (units * KeyboardGeometry.UnitPixels) - (2 * KeyboardGeometry.KeyMargin);
        _color = color;
    }
}

/// <summary>A drawn row of keys, indented from its block's left edge.</summary>
public sealed class KeyRowViewModel
{
    /// <param name="indentUnits">Left offset in key-widths.</param>
    /// <param name="keys">The keys in the order they are drawn.</param>
    public KeyRowViewModel(double indentUnits, IReadOnlyList<KeySlotViewModel> keys)
    {
        Indent = new System.Windows.Thickness(indentUnits * KeyboardGeometry.UnitPixels, 0, 0, 0);
        Keys = keys;
    }

    /// <summary>Left offset in pixels, as a margin the row can bind straight to.</summary>
    public System.Windows.Thickness Indent { get; }

    /// <summary>The keys in this row, left to right.</summary>
    public IReadOnlyList<KeySlotViewModel> Keys { get; }
}

/// <summary>A cluster of rows drawn beside the others - the main block, the arrows, the numpad.</summary>
public sealed class KeyBlockViewModel
{
    /// <param name="name">What the cluster is, for accessibility rather than display.</param>
    /// <param name="indentUnits">Gap between this cluster and the one before it.</param>
    /// <param name="rows">The cluster's rows, top to bottom.</param>
    public KeyBlockViewModel(string name, double indentUnits, IReadOnlyList<KeyRowViewModel> rows)
    {
        Name = name;
        Indent = new System.Windows.Thickness(indentUnits * KeyboardGeometry.UnitPixels, 0, 0, 0);
        Rows = rows;
    }

    /// <summary>The cluster's name.</summary>
    public string Name { get; }

    /// <summary>Gap to the left of the cluster, as a margin.</summary>
    public System.Windows.Thickness Indent { get; }

    /// <summary>The cluster's rows, top to bottom.</summary>
    public IReadOnlyList<KeyRowViewModel> Rows { get; }
}

/// <summary>
/// The per-key editor: a picture of the keyboard that is painted with a brush colour and then
/// written to the hardware as 128 slots.
/// </summary>
/// <remarks>
/// <para>
/// Two orders meet here and are deliberately kept apart. <see cref="KeyboardGeometry"/> says where
/// a key is drawn; <see cref="KeyLayout"/> says which of the 128 slots lights it. They are joined
/// only by the key's name, because the slots are an electrical scan matrix and reading nothing like
/// a keyboard - <c>]</c> arrives 16 slots before <c>[</c>. Every key the editor offers therefore
/// carries the slot it resolved to, and <see cref="ToSlotArray"/> writes each key's colour into
/// that slot and nowhere else.
/// </para>
/// <para>
/// The unpopulated slots - a third of the report on this keyboard - are not offered at all, since
/// there is no key there to paint. They still go out, as black, because the hardware wants all 128
/// every time.
/// </para>
/// <para>
/// Unlike the effect panel this one stages rather than previews. A per-key write is four paced
/// reports, and a brush stroke across a row would queue dozens of them; the owner paints freely and
/// presses Apply once.
/// </para>
/// </remarks>
public partial class PerKeyEditorViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Action<BannerKind, string> _banner;
    private readonly Func<int> _brightness;
    private readonly CancellationToken _shutdown;

    private bool _syncingHex;

    [ObservableProperty] private MediaColor _brushColor = MediaColor.FromRgb(0xFF, 0x7A, 0x1A);
    [ObservableProperty] private string _brushHex = "#FF7A1A";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Whether a drag is under way, so that crossing a key paints it.</summary>
    [ObservableProperty] private bool _isPainting;

    /// <summary>Every paintable key, one per populated slot, in the order it is drawn.</summary>
    public ObservableCollection<KeySlotViewModel> Keys { get; } = new();

    /// <summary>The same keys arranged into the picture the owner clicks on.</summary>
    public IReadOnlyList<KeyBlockViewModel> Blocks { get; }

    /// <summary>The colours the brush can be set to without typing hex.</summary>
    public IReadOnlyList<MediaColor> Swatches => ColorText.Swatches;

    /// <param name="services">The app's services; only the lighting and settings halves are used.</param>
    /// <param name="banner">Where a failed write is reported - <see cref="MainViewModel.SetBanner"/>.</param>
    /// <param name="brightness">The panel's live brightness, which a per-key write also carries.</param>
    /// <param name="shutdown">Cancelled when the window is closing, mid-sequence.</param>
    public PerKeyEditorViewModel(
        AppServices services,
        Action<BannerKind, string> banner,
        Func<int> brightness,
        CancellationToken shutdown = default)
    {
        _s = services;
        _banner = banner;
        _brightness = brightness;
        _shutdown = shutdown;

        Blocks = BuildPicture(_s.Lighting.Layout);
        Load(_s.Settings.Lighting.PerKeyColors);
    }

    // ---- Building the picture -------------------------------------------------------

    /// <summary>
    /// Turns the drawn arrangement into keys, dropping any the active layout does not define.
    /// That is how one table serves both slot orders: ENG-US has no <c>#</c> and ENG-UK no
    /// <c>\</c>, and each simply loses the key the other one has.
    /// </summary>
    private IReadOnlyList<KeyBlockViewModel> BuildPicture(KeyLayout layout)
    {
        var blocks = new List<KeyBlockViewModel>();
        foreach (var block in KeyboardGeometry.Blocks)
        {
            var rows = new List<KeyRowViewModel>();
            foreach (var row in block.Rows)
            {
                var keys = new List<KeySlotViewModel>();
                foreach (var placement in row.Keys)
                {
                    var slot = layout.IndexOf(placement.Name);
                    if (slot < 0) continue;
                    var key = new KeySlotViewModel(slot, placement.Name, placement.Units, default);
                    keys.Add(key);
                    Keys.Add(key);
                }
                if (keys.Count > 0) rows.Add(new KeyRowViewModel(row.IndentUnits, keys));
            }
            if (rows.Count > 0) blocks.Add(new KeyBlockViewModel(block.Name, block.IndentUnits, rows));
        }
        return blocks;
    }

    /// <summary>
    /// Repaints the existing keys from a slot array. The keys themselves are never rebuilt: they
    /// are what the picture is bound to, and replacing them on every read would throw the whole
    /// visual tree away to change 101 colours.
    /// </summary>
    private void Load(IReadOnlyList<RgbColor> colors)
    {
        foreach (var key in Keys)
        {
            var c = key.Slot < colors.Count ? colors[key.Slot] : RgbColor.Black;
            key.Color = MediaColor.FromRgb(c.R, c.G, c.B);
            // The outline marks the key painted last, and nothing here was painted: leaving it
            // would point at a key whose colour has just been replaced by the keyboard's own.
            key.IsSelected = false;
        }
    }

    /// <summary>Expands the painted keys back into the full 128-slot array the hardware expects.</summary>
    private RgbColor[] ToSlotArray()
    {
        var colors = new RgbColor[KeyLayout.SlotCount];
        foreach (var key in Keys)
            colors[key.Slot] = new RgbColor(key.Color.R, key.Color.G, key.Color.B);
        return colors;
    }

    // ---- Painting -------------------------------------------------------------------

    /// <summary>Paints one key and marks it, whether or not a drag is under way.</summary>
    [RelayCommand]
    private void Paint(KeySlotViewModel? key)
    {
        if (key is null) return;

        foreach (var other in Keys) other.IsSelected = false;
        key.IsSelected = true;
        key.Color = BrushColor;
        // Named rather than counted: painting one key at a time and checking which key lights is
        // how the owner settles whether the recovered slot order matches their keyboard.
        StatusText = $"Painted {key.Name} · slot {key.Slot}";
    }

    /// <summary>Starts a drag. Called when the mouse goes down on the picture.</summary>
    public void BeginPaint() => IsPainting = true;

    /// <summary>Ends a drag. Called when the mouse comes up, or leaves the picture.</summary>
    public void EndPaint() => IsPainting = false;

    /// <summary>Paints a key the pointer crossed, but only during a drag.</summary>
    public void PaintOver(KeySlotViewModel? key)
    {
        if (IsPainting) Paint(key);
    }

    [RelayCommand]
    private void FillAll()
    {
        foreach (var key in Keys) key.Color = BrushColor;
        StatusText = "Filled every key";
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (var key in Keys) key.Color = MediaColor.FromRgb(0, 0, 0);
        StatusText = "Cleared";
    }

    // ---- The hardware ---------------------------------------------------------------

    /// <summary>Writes the painting to the keyboard and switches it to custom colours.</summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!_s.KeyboardPresent || IsBusy) return;
        IsBusy = true;
        try
        {
            var colors = ToSlotArray();
            var result = await _s.Lighting.ApplyPerKeyAsync(colors, _brightness(), _shutdown);
            if (!result.Success)
            {
                StatusText = "Failed";
                _banner(BannerKind.Error, result.Error!);
                return;
            }

            var saved = _s.Settings.Lighting;
            saved.PerKeyColors = colors.ToList();
            saved.Effect = LightEffect.Custom;
            saved.BrightnessPercent = _brightness();
            _s.Store.Save(_s.Settings);
            StatusText = "Per-key colours applied";
        }
        catch (OperationCanceledException)
        {
            // The window is closing under the sequence. Nothing was asked for and nothing is shown.
        }
        finally { IsBusy = false; }
    }

    /// <summary>Loads the colours the keyboard is currently holding into the editor.</summary>
    [RelayCommand]
    private async Task ReadFromKeyboardAsync()
    {
        if (!_s.KeyboardPresent || IsBusy) return;
        IsBusy = true;
        try
        {
            var colors = await _s.Lighting.ReadPerKeyAsync(_shutdown);
            if (colors is null)
            {
                // Deliberately not a banner: a keyboard that will not answer 0x86 is a limitation
                // to notice, not a failure of something the owner had already committed to.
                StatusText = "The keyboard did not answer";
                return;
            }

            Load(colors);
            StatusText = "Read the keyboard's current colours";
        }
        catch (OperationCanceledException)
        {
        }
        finally { IsBusy = false; }
    }

    // ---- The brush colour and its hex box -------------------------------------------

    partial void OnBrushColorChanged(MediaColor value)
    {
        if (_syncingHex) return;
        _syncingHex = true;
        try { BrushHex = ColorText.Format(value); }
        finally { _syncingHex = false; }
    }

    partial void OnBrushHexChanged(string value)
    {
        if (_syncingHex) return;
        // Silently ignored while it does not parse: the box is bound live and sees every
        // half-typed value on the way to a whole one.
        if (!ColorText.TryParse(value, out var color)) return;
        _syncingHex = true;
        try { BrushColor = color; }
        finally { _syncingHex = false; }
    }
}
