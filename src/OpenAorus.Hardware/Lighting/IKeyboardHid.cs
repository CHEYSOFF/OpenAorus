namespace OpenAorus.Hardware.Lighting;

public sealed record KeyboardIdentity(ushort Vid, ushort Pid, string Path, string Product);

/// <summary>
/// The only door to the keyboard's lighting collection. Real implementation:
/// <see cref="KeyboardHid"/>. Unlike the fan path this needs no elevation.
/// </summary>
public interface IKeyboardHid : IDisposable
{
    bool IsPresent { get; }
    KeyboardIdentity? Identity { get; }
    bool SetFeature(byte[] report);
    byte[]? GetFeature();
}
