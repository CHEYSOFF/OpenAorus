namespace OpenAorus.Hardware.Lighting;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor White => new(0xFF, 0xFF, 0xFF);
    public static RgbColor Black => new(0x00, 0x00, 0x00);
}

public enum LightDirection { Right, Left, Up, Down, Clockwise, CounterClockwise }

public sealed record EffectParameters(
    LightEffect Effect,
    RgbColor Color,
    RgbColor SecondColor,
    int SpeedPercent,
    int BrightnessPercent,
    LightDirection Direction,
    bool Random)
{
    public static EffectParameters Default(LightEffect effect) => new(
        Effect: effect,
        Color: RgbColor.White,
        SecondColor: new RgbColor(0x00, 0x00, 0xFF),
        SpeedPercent: 50,
        BrightnessPercent: 50,
        Direction: LightDirection.Right,
        Random: false);
}
