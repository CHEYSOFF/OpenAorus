using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public sealed class FakeKeyboardHid : IKeyboardHid
{
    public List<byte[]> Written { get; } = new();
    public Queue<byte[]> Responses { get; } = new();
    public bool FailNextWrite { get; set; }
    public bool IsPresent { get; set; } = true;
    public KeyboardIdentity? Identity { get; set; } =
        new(0x1044, 0x7A3D, @"\\?\hid#vid_1044&pid_7a3d&mi_02&col06#fake", "Fusion RGB KB");

    public bool SetFeature(byte[] report)
    {
        Written.Add((byte[])report.Clone());
        if (!FailNextWrite) return true;
        FailNextWrite = false;
        return false;
    }

    public byte[]? GetFeature() => Responses.Count > 0 ? Responses.Dequeue() : null;

    public void Dispose() { }

    /// <summary>The command byte of the Nth report written.</summary>
    public byte Command(int index) => Written[index][1];
}
