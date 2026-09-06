using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Config;

public sealed class AppSettings
{
    private LightingSettings _lighting = new();

    public FanMode Mode { get; set; } = FanMode.Normal;
    public int FixedPercent { get; set; } = 50;
    public List<FanCurvePoint> Curve { get; set; } = FanCurve.Default.Points.ToList();
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

    public FanCurve ToCurve() => new(Curve);
}
