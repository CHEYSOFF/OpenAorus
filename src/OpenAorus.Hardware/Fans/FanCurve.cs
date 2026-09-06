namespace OpenAorus.Hardware.Fans;

public readonly record struct FanCurvePoint(int Temperature, int DutyPercent);

/// <summary>Temperature→duty table that the EC runs by itself (SetFanIndexValue slots 0..14).</summary>
public sealed class FanCurve
{
    public const int MinPoints = 2;
    public const int MaxPoints = 15;

    public IReadOnlyList<FanCurvePoint> Points { get; }

    public FanCurve(IEnumerable<FanCurvePoint> points) => Points = points.ToList();

    public static FanCurve Default { get; } = new(new[]
    {
        new FanCurvePoint(40, 25),
        new FanCurvePoint(50, 30),
        new FanCurvePoint(60, 40),
        new FanCurvePoint(70, 55),
        new FanCurvePoint(80, 75),
        new FanCurvePoint(90, 100),
    });

    public bool IsValid => Validate().Count == 0;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Points.Count < MinPoints) errors.Add($"Curve needs at least {MinPoints} points.");
        if (Points.Count > MaxPoints) errors.Add($"Curve can have at most {MaxPoints} points.");
        for (var i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            if (p.Temperature is < 0 or > 100 || p.DutyPercent is < 0 or > 100)
                errors.Add($"Point {i + 1}: temperature and duty must be 0-100.");
            if (i > 0)
            {
                if (p.Temperature <= Points[i - 1].Temperature)
                    errors.Add($"Point {i + 1}: temperatures must be strictly increasing.");
                if (p.DutyPercent < Points[i - 1].DutyPercent)
                    errors.Add($"Point {i + 1}: duty must not decrease.");
            }
        }
        return errors;
    }
}
