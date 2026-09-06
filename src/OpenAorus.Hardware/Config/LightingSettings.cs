using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Config;

/// <summary>A named snapshot of an effect and its parameters, optionally with per-key colours.</summary>
public sealed class LightingPreset
{
    /// <summary>What the owner called it. A preset with no name cannot be picked and is dropped on load.</summary>
    public string Name { get; set; } = "";

    /// <summary>The effect this preset selects.</summary>
    public LightEffect Effect { get; set; } = LightEffect.Static;

    /// <summary>Primary colour, for the effects that take one.</summary>
    public RgbColor Color { get; set; } = RgbColor.White;

    /// <summary>Second colour, for the two-colour effects.</summary>
    public RgbColor SecondColor { get; set; } = new(0x00, 0x00, 0xFF);

    /// <summary>0-100, slow to fast.</summary>
    public int SpeedPercent { get; set; } = 50;

    /// <summary>0-100.</summary>
    public int BrightnessPercent { get; set; } = 50;

    /// <summary>Travel direction, for the effects that take one.</summary>
    public LightDirection Direction { get; set; } = LightDirection.Right;

    /// <summary>Whether the effect picks its own colours.</summary>
    public bool Random { get; set; }

    /// <summary>Exactly <see cref="KeyLayout.SlotCount"/> colours, or null for a preset that is not per-key.</summary>
    public List<RgbColor>? PerKeyColors { get; set; }

    /// <summary>This preset as the parameters a lighting write takes.</summary>
    public EffectParameters ToParameters() =>
        new(Effect, Color, SecondColor, SpeedPercent, BrightnessPercent, Direction, Random);
}

/// <summary>
/// The lighting half of <see cref="AppSettings"/>: what to restore at startup, plus the
/// owner's saved presets.
/// </summary>
/// <remarks>
/// Everything here arrives from a file on disk that anyone can edit and that older or newer
/// versions of the app may have written, so <see cref="Repair"/> exists to bring a loaded
/// instance back inside the ranges the hardware layer accepts. <see cref="SettingsStore.Load"/>
/// calls it; nothing else needs to.
/// </remarks>
public sealed class LightingSettings
{
    private List<RgbColor> _perKeyColors = new();
    private List<LightingPreset> _presets = new();

    /// <summary>The effect to restore at startup.</summary>
    public LightEffect Effect { get; set; } = LightEffect.Static;

    /// <summary>Primary colour, for the effects that take one.</summary>
    public RgbColor Color { get; set; } = RgbColor.White;

    /// <summary>Second colour, for the two-colour effects.</summary>
    public RgbColor SecondColor { get; set; } = new(0x00, 0x00, 0xFF);

    /// <summary>0-100, slow to fast.</summary>
    public int SpeedPercent { get; set; } = 50;

    /// <summary>0-100.</summary>
    public int BrightnessPercent { get; set; } = 50;

    /// <summary>Travel direction, for the effects that take one.</summary>
    public LightDirection Direction { get; set; } = LightDirection.Right;

    /// <summary>Whether the effect picks its own colours.</summary>
    public bool Random { get; set; }

    /// <summary>
    /// The saved custom colours, in slot order: either empty or exactly
    /// <see cref="KeyLayout.SlotCount"/> long. Never null, so a <c>"PerKeyColors": null</c>
    /// in the file cannot turn a startup apply into a null reference.
    /// </summary>
    public List<RgbColor> PerKeyColors
    {
        get => _perKeyColors;
        set => _perKeyColors = value ?? new List<RgbColor>();
    }

    /// <summary>Set when the owner's keyboard disagrees with the slot order implied by its product id.</summary>
    public KeyboardLayout? LayoutOverride { get; set; }

    /// <summary>The owner's saved presets. Never null.</summary>
    public List<LightingPreset> Presets
    {
        get => _presets;
        set => _presets = value ?? new List<LightingPreset>();
    }

    /// <summary>These settings as the parameters a lighting write takes.</summary>
    public EffectParameters ToParameters() =>
        new(Effect, Color, SecondColor, SpeedPercent, BrightnessPercent, Direction, Random);

    /// <summary>Copies the effect and its parameters in, leaving the per-key colours and presets alone.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="p"/> is null.</exception>
    public void From(EffectParameters p)
    {
        ArgumentNullException.ThrowIfNull(p);
        Effect = p.Effect;
        Color = p.Color;
        SecondColor = p.SecondColor;
        SpeedPercent = p.SpeedPercent;
        BrightnessPercent = p.BrightnessPercent;
        Direction = p.Direction;
        Random = p.Random;
    }

    /// <summary>
    /// Replaces any value that could not have come from this app with its default, and reports
    /// whether it had to.
    /// </summary>
    /// <remarks>
    /// A hand-edited or downgraded settings file can carry an effect id no firmware defines, a
    /// percentage well outside 0-100, or a half-length colour list. The first of those throws out
    /// of <see cref="EffectPacket.Build"/>; <see cref="LightingController"/> turns that into a
    /// failed result rather than a crash, but the owner is then stuck with lighting that refuses
    /// to apply until they find the file. Repairing on load means they get working defaults and a
    /// notice instead. The return value is what drives that notice, so this stays honest about
    /// having changed nothing.
    /// </remarks>
    /// <returns>True if any value was replaced.</returns>
    public bool Repair()
    {
        var repaired = false;

        if (!Enum.IsDefined(Effect)) { Effect = LightEffect.Static; repaired = true; }
        if (!Enum.IsDefined(Direction)) { Direction = LightDirection.Right; repaired = true; }
        if (LayoutOverride is { } layout && !Enum.IsDefined(layout)) { LayoutOverride = null; repaired = true; }
        if (NeedsClamp(SpeedPercent, out var speed)) { SpeedPercent = speed; repaired = true; }
        if (NeedsClamp(BrightnessPercent, out var brightness)) { BrightnessPercent = brightness; repaired = true; }

        // A partial colour list cannot be sent - PerKeyPacket demands all 128 - and half the
        // owner's colours are worse than none, so it goes rather than being padded with black.
        if (PerKeyColors.Count is not (0 or KeyLayout.SlotCount))
        {
            PerKeyColors = new List<RgbColor>();
            repaired = true;
        }

        // Backwards, so dropping an unusable preset does not skip the one after it.
        for (var i = Presets.Count - 1; i >= 0; i--)
        {
            var preset = Presets[i];
            if (preset is null || string.IsNullOrWhiteSpace(preset.Name))
            {
                // A nameless preset cannot be told apart in the list, so there is nothing to keep.
                Presets.RemoveAt(i);
                repaired = true;
                continue;
            }

            repaired |= RepairPreset(preset);
        }

        return repaired;
    }

    private static bool RepairPreset(LightingPreset preset)
    {
        var repaired = false;
        if (!Enum.IsDefined(preset.Effect)) { preset.Effect = LightEffect.Static; repaired = true; }
        if (!Enum.IsDefined(preset.Direction)) { preset.Direction = LightDirection.Right; repaired = true; }
        if (NeedsClamp(preset.SpeedPercent, out var speed)) { preset.SpeedPercent = speed; repaired = true; }
        if (NeedsClamp(preset.BrightnessPercent, out var brightness)) { preset.BrightnessPercent = brightness; repaired = true; }
        if (preset.PerKeyColors is { } colors && colors.Count != KeyLayout.SlotCount)
        {
            preset.PerKeyColors = null;
            repaired = true;
        }
        return repaired;
    }

    private static bool NeedsClamp(int percent, out int clamped)
    {
        clamped = Math.Clamp(percent, 0, 100);
        return clamped != percent;
    }

    /// <summary>
    /// The presets every install starts with: Off, Warm White and Aorus Orange.
    /// </summary>
    /// <remarks>
    /// Fresh instances each call. These are seeds the owner is meant to apply and adapt, and
    /// handing out one shared mutable object per preset would let an edit in the UI rewrite the
    /// built-in for the life of the process.
    /// </remarks>
    public static IReadOnlyList<LightingPreset> BuiltInPresets => new[]
    {
        new LightingPreset { Name = "Off", Effect = LightEffect.Static, Color = RgbColor.Black, BrightnessPercent = 0 },
        new LightingPreset { Name = "Warm White", Effect = LightEffect.Static, Color = new RgbColor(0xFF, 0xC8, 0x80), BrightnessPercent = 60 },
        new LightingPreset { Name = "Aorus Orange", Effect = LightEffect.Static, Color = new RgbColor(0xFF, 0x7A, 0x1A), BrightnessPercent = 70 },
    };
}
