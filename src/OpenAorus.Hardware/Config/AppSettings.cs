using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Config;

public sealed class AppSettings
{
    private LightingSettings _lighting = new();
    private HotkeySettings _hotkeys = new();
    private SchemaRecord _schema = new();
    private List<FanCurvePoint> _curve = FanCurve.Default.Points.ToList();

    public FanMode Mode { get; set; } = FanMode.Normal;
    public int FixedPercent { get; set; } = 50;

    /// <summary>The custom curve's points. Never null: a file written with <c>"Curve": null</c>
    /// loads with an empty list, which <see cref="RepairFans"/> then replaces.</summary>
    public List<FanCurvePoint> Curve
    {
        get => _curve;
        set => _curve = value ?? new List<FanCurvePoint>();
    }

    public bool ChargeLimitEnabled { get; set; }
    public int ChargeStopPercent { get; set; } = 80;
    public int PollIntervalVisibleMs { get; set; } = 1000;
    public int PollIntervalHiddenMs { get; set; } = 5000;
    public bool StartWithWindows { get; set; }
    public OpenAorus.Hardware.Platform.GccTakeoverState? Takeover { get; set; }

    /// <summary>Keyboard lighting. Never null: a file written with <c>"Lighting": null</c>, or one
    /// from v0.1 that has no lighting section at all, still loads with usable defaults.</summary>
    public LightingSettings Lighting
    {
        get => _lighting;
        set => _lighting = value ?? new LightingSettings();
    }

    /// <summary>Fn hotkeys and the overlay. Never null: a file written with <c>"Hotkeys": null</c>,
    /// or one from v0.1 or v0.2 that has no hotkey section at all, still loads with usable
    /// defaults - the channels on and every overlay off.</summary>
    public HotkeySettings Hotkeys
    {
        get => _hotkeys;
        set => _hotkeys = value ?? new HotkeySettings();
    }

    /// <summary>What was done to the WMI schema and what the hardware gates proved about it. Never
    /// null: a file written with <c>"Schema": null</c>, or one from before the gates existed that
    /// has no schema section at all, still loads with a record that claims nothing - which is the
    /// right default, since claiming nothing is what keeps writes locked.</summary>
    public SchemaRecord Schema
    {
        get => _schema;
        set => _schema = value ?? new SchemaRecord();
    }

    public FanCurve ToCurve() => new(Curve);

    /// <summary>
    /// Lifts a saved fan setting that would leave the machine uncooled back inside
    /// <see cref="FanSafety"/>, and reports whether it had to.
    /// </summary>
    /// <remarks>
    /// The same job <see cref="LightingSettings.Repair"/> does, and called from the same place -
    /// <see cref="SettingsStore.Load"/> - for a sharper reason. A settings file written before
    /// these floors existed, or edited by hand, can hold <c>FixedPercent: 0</c> or a curve that
    /// never ramps; <c>--apply</c> reads that file and writes it straight to the controller with
    /// no UI anywhere in the process. Repairing on load is what stops an old file from being the
    /// way back into the state the floors exist to prevent.
    ///
    /// A too-low Fixed duty has an obvious nearest safe value, so it is raised. A curve does not:
    /// there is no single edit that makes an arbitrary unsafe table safe while still resembling
    /// what the owner asked for, so it is replaced wholesale with <see cref="FanCurve.Default"/>.
    /// Either way the return value drives the notice that tells the owner, so this stays honest
    /// about having changed nothing.
    /// </remarks>
    /// <returns>True if any fan value was replaced.</returns>
    public bool RepairFans()
    {
        var repaired = false;

        if (!FanSafety.IsFixedPercentSafe(FixedPercent) || FixedPercent > 100)
        {
            FixedPercent = FanSafety.ClampFixedPercent(FixedPercent);
            repaired = true;
        }

        if (!ToCurve().IsValid)
        {
            Curve = FanCurve.Default.Points.ToList();
            repaired = true;
        }

        return repaired;
    }
}
