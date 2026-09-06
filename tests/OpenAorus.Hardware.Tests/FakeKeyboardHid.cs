using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public sealed class FakeKeyboardHid : IKeyboardHid
{
    private readonly List<byte[]> _written = new();

    /// <summary>
    /// Every report written so far, oldest first. A snapshot taken under the same lock the
    /// writes take, so it is safe to read while a drain loop is still writing - enumerating
    /// the live list mid-burst throws, and did once turn a real regression into a pass.
    /// </summary>
    public IReadOnlyList<byte[]> Written { get { lock (_written) return _written.ToArray(); } }

    public Queue<byte[]> Responses { get; } = new();
    public bool FailNextWrite { get; set; }

    /// <summary>Zero-based index of a write to reject, for sequences where the interesting
    /// failure is not the first report.</summary>
    public int? FailWriteAt { get; set; }

    public bool IsPresent { get; set; } = true;
    public KeyboardIdentity? Identity { get; set; } =
        new(0x1044, 0x7A3D, @"\\?\hid#vid_1044&pid_7a3d&mi_02&col06#fake", "Fusion RGB KB");

    public bool SetFeature(byte[] report)
    {
        if (report.Length != KeyboardHid.ReportLength)
            throw new ArgumentException($"Report must be {KeyboardHid.ReportLength} bytes.", nameof(report));
        lock (_written)
        {
            var index = _written.Count;
            _written.Add((byte[])report.Clone());
            // Consume FailNextWrite here too: setting both knobs must still fail exactly one write.
            if (FailWriteAt == index) { FailNextWrite = false; return false; }
            if (!FailNextWrite) return true;
            FailNextWrite = false;
            return false;
        }
    }

    public byte[]? GetFeature() => Responses.Count > 0 ? Responses.Dequeue() : null;

    public void Dispose() { }

    /// <summary>Forgets the reports written so far, so a test can assert on what follows.</summary>
    public void ClearWritten() { lock (_written) _written.Clear(); }

    /// <summary>The command byte of the Nth report written.</summary>
    public byte Command(int index) { lock (_written) return _written[index][1]; }
}
