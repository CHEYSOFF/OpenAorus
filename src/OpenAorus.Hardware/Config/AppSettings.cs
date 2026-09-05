using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Config;

public sealed class AppSettings
{
    public FanMode Mode { get; set; } = FanMode.Normal;
    public int FixedPercent { get; set; } = 50;
    public List<FanCurvePoint> Curve { get; set; } = FanCurve.Default.Points.ToList();
    public bool ChargeLimitEnabled { get; set; }
    public int ChargeStopPercent { get; set; } = 80;
    public int PollIntervalVisibleMs { get; set; } = 1000;
    public int PollIntervalHiddenMs { get; set; } = 5000;
    public bool StartWithWindows { get; set; }

    public FanCurve ToCurve() => new(Curve);
}
