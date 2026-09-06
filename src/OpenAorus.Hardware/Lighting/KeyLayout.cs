namespace OpenAorus.Hardware.Lighting;

/// <summary>The keyboard variants whose slot order differs.</summary>
public enum KeyboardLayout { EngUs, EngUk }

/// <summary>
/// Maps the keyboard's 128 lighting slots to key names. Slot order is a property of the
/// firmware, not of the visual layout, and it differs between the US and UK variants.
/// Gigabyte's own software flags product 0x7A3D as the UK order and 0x7A3C as US;
/// that mapping is unverified on hardware, so the app exposes a manual override.
///
/// Both orders are transcribed verbatim from docs/research/ione-keymap-eng-{us,uk}.txt,
/// which were recovered from Gigabyte's software. Names that look wrong (the bracket
/// pair reads reversed against the physical layout) are left as recovered: nothing has
/// been checked against hardware yet, so the source is the more trustworthy of the two.
/// </summary>
public sealed class KeyLayout
{
    /// <summary>Slots carried by one lighting report.</summary>
    public const int SlotCount = 128;

    /// <summary>Name given to a slot the keyboard does not populate.</summary>
    public const string Unused = "N/A";

    private static readonly string[] EngUsSlots =
    {
        "N/A", "N/A", "N/A", "N/A", "Ctrl-R", "PgUp", "Ctrl-L", "F5",
        "Q", "Tab", "A", "ESC", "Z", "N/A", "~", "1",
        "W", "Caps", "S", "N/A", "X", "N/A", "F1", "2",
        "E", "F3", "D", "F4", "C", "N/A", "F2", "3",
        "R", "T", "F", "G", "V", "B", "5", "4",
        "U", "Y", "J", "H", "M", "N", "6", "7",
        "I", "]", "K", "F6", ",", "N/A", "=", "8",
        "O", "F7", "L", "N/A", ".", "Menu", "F8", "9",
        "P", "[", ";", "'", "N/A", "/", "-", "0",
        "N/A", "N/A", "N/A", "Alt-L", "N/A", "Alt-R", "N/A", "Pause",
        "N/A", "Backspace", "\\", "F11", "Enter", "F12", "F9", "F10",
        "Num-7", "Num-4", "Num-1", "Space", "NumLk", "Down", "Home", "N/A",
        "Num-8", "Num-5", "Num-2", "Num-0", "Num-/", "Right", "N/A", "Del",
        "Num-9", "Num-6", "Num-3", "Num-.", "Num-*", "Num--", "N/A", "PgDn",
        "Num-+", "N/A", "Num-Enter", "Up", "N/A", "Left", "N/A", "End",
        "N/A", "Shift-L", "Shift-R", "N/A", "WinKey", "Fn", "N/A", "N/A",
    };

    private static readonly string[] EngUkSlots =
    {
        "N/A", "N/A", "N/A", "N/A", "Ctrl-R", "PgUp", "Ctrl-L", "F5",
        "Q", "Tab", "A", "ESC", "Z", "N/A", "~", "1",
        "W", "Caps", "S", "N/A", "X", "N/A", "F1", "2",
        "E", "F3", "D", "F4", "C", "N/A", "F2", "3",
        "R", "T", "F", "G", "V", "B", "5", "4",
        "U", "Y", "J", "H", "M", "N", "6", "7",
        "I", "]", "K", "F6", ",", "N/A", "=", "8",
        "O", "F7", "L", "N/A", ".", "Menu", "F8", "9",
        "P", "[", ";", "'", "#", "/", "-", "0",
        "N/A", "N/A", "N/A", "Alt-L", "N/A", "Alt-R", "N/A", "Pause",
        "N/A", "Backspace", "N/A", "F11", "Enter", "F12", "F9", "F10",
        "Num-7", "Num-4", "Num-1", "Space", "NumLk", "Down", "Home", "N/A",
        "Num-8", "Num-5", "Num-2", "Num-0", "Num-/", "Right", "N/A", "Del",
        "Num-9", "Num-6", "Num-3", "Num-.", "Num-*", "Num--", "N/A", "PgDn",
        "Num-+", "N/A", "Num-Enter", "Up", "N/A", "Left", "N/A", "End",
        "N/A", "Shift-L", "Shift-R", "N/A", "WinKey", "Fn", "N/A", "N/A",
    };

    private static readonly KeyLayout Us = new(KeyboardLayout.EngUs, EngUsSlots);
    private static readonly KeyLayout Uk = new(KeyboardLayout.EngUk, EngUkSlots);

    /// <summary>The variant this map describes.</summary>
    public KeyboardLayout Layout { get; }

    /// <summary>Key name per slot, in slot order, <see cref="Unused"/> where unpopulated.</summary>
    public IReadOnlyList<string> Slots { get; }

    private KeyLayout(KeyboardLayout layout, string[] slots)
    {
        if (slots.Length != SlotCount)
            throw new InvalidOperationException($"{layout} layout must define exactly {SlotCount} slots, found {slots.Length}.");
        Layout = layout;
        Slots = slots;
    }

    /// <summary>The map for a variant.</summary>
    public static KeyLayout For(KeyboardLayout layout) => layout == KeyboardLayout.EngUk ? Uk : Us;

    /// <summary>Gigabyte's software treats 0x7A3D as the UK slot order and 0x7A3C as US.</summary>
    public static KeyLayout ForProduct(ushort productId) => productId == 0x7A3D ? Uk : Us;

    /// <summary>The key at a slot.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The slot is outside the report.</exception>
    public string NameAt(int slot)
    {
        if (slot is < 0 or >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        return Slots[slot];
    }

    /// <summary>The slot a key occupies, or -1 for an unknown or unused name.</summary>
    public int IndexOf(string keyName)
    {
        if (string.IsNullOrEmpty(keyName) || keyName == Unused) return -1;
        for (var i = 0; i < SlotCount; i++)
            if (string.Equals(Slots[i], keyName, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Every populated slot, in slot order.</summary>
    public IEnumerable<(int Slot, string Name)> RealKeys
    {
        get
        {
            for (var i = 0; i < SlotCount; i++)
                if (Slots[i] != Unused) yield return (i, Slots[i]);
        }
    }
}
