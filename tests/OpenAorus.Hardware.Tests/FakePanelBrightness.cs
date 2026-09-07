using OpenAorus.Hardware.Display;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// A panel that answers out of memory, the way <see cref="FakeKeyboardHid"/> and
/// <see cref="FakeGigabyteWmi"/> do for their own hardware.
/// </summary>
/// <remarks>
/// The point of the seam. Nothing else in the app can read or write the panel, so every question
/// about stepping, clamping and a machine with no controllable panel is answered here rather than
/// on a bench with a screen that has to be watched by eye.
/// </remarks>
public sealed class FakePanelBrightness : IPanelBrightness
{
    /// <summary>A dense ladder, which is what most laptop panels report.</summary>
    public static IReadOnlyList<int> DenseLadder { get; } =
        Enumerable.Range(0, 101).ToArray();

    /// <summary>What the panel says about itself, or null for a machine with no controllable one.</summary>
    public PanelBrightnessState? State { get; set; } = new(50, DenseLadder);

    /// <summary>Every level written, oldest first.</summary>
    public List<int> Written { get; } = new();

    /// <summary>How many times the level was read back.</summary>
    public int Reads { get; private set; }

    /// <summary>Rejects the next write, the way a panel that has gone away would.</summary>
    public bool FailNextSet { get; set; }

    /// <summary>Thrown out of <see cref="Read"/>, to stand in for a WMI provider that is unwell.</summary>
    public Exception? ThrowOnRead { get; set; }

    /// <summary>Thrown out of <see cref="Set"/>, for the same reason.</summary>
    public Exception? ThrowOnSet { get; set; }

    public PanelBrightnessState? Read()
    {
        Reads++;
        if (ThrowOnRead is { } error) throw error;
        return State;
    }

    public bool Set(int level)
    {
        if (ThrowOnSet is { } error) throw error;

        Written.Add(level);
        if (FailNextSet)
        {
            FailNextSet = false;
            return false;
        }

        if (State is { } state) State = state with { Current = level };
        return true;
    }
}
