namespace OpenAorus.Hardware.Lighting;

/// <summary>Effect ids as written to byte 10 of the 0x02 report.</summary>
public enum LightEffect : byte
{
    Static = 0x00,
    Breathing = 0x01,
    Flow = 0x02,
    Firework = 0x03,
    Ripple = 0x04,
    Rain = 0x05,
    Cycling = 0x06,
    Trigger = 0x07,
    Pulse = 0x08,
    Radar = 0x09,
    StarShining = 0x0A,
    Wave = 0x0B,
    Cross = 0x0C,
    Dragonstrike = 0x0D,
    Bloom = 0x0E,
    Spiral = 0x0F,
    Merge = 0x10,
    Crash = 0x11,
    Custom = 0x12,
}
