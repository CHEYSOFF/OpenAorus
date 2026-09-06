namespace OpenAorus.App.ViewModels;

/// <summary>One key's place in the picture: the name it carries, and how many key-widths it spans.</summary>
/// <param name="Name">The <see cref="Hardware.Lighting.KeyLayout"/> name, and the only thing that
/// ties a drawn key to a lighting slot.</param>
/// <param name="Units">Width in key-widths; 1 is a letter key.</param>
public sealed record KeyPlacement(string Name, double Units = 1.0);

/// <summary>A row of keys within a block, shifted right by <paramref name="IndentUnits"/>.</summary>
public sealed record KeyRowPlacement(double IndentUnits, IReadOnlyList<KeyPlacement> Keys);

/// <summary>A cluster of rows - the main block, the arrows, the navigation column, the numpad.</summary>
public sealed record KeyBlockPlacement(string Name, double IndentUnits, IReadOnlyList<KeyRowPlacement> Rows);

/// <summary>
/// Where each key is drawn on the picture of the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// This is a separate table from <see cref="Hardware.Lighting.KeyLayout"/> on purpose, and the two
/// must never be reconciled by editing the other one. The 128 lighting slots are an electrical scan
/// matrix: they group by column, so <c>]</c> arrives at slot 49 and <c>[</c> at slot 65, and
/// <c>=</c> before <c>-</c>. Reordering the slot arrays to make a left-to-right picture would land
/// every colour on the wrong key. So the picture is described here in the order a person sees, and
/// joined to the wire by name through <see cref="Hardware.Lighting.KeyLayout.IndexOf"/>.
/// </para>
/// <para>
/// The table names both layouts' odd key out - <c>\</c> above Enter for ENG-US, <c>#</c> beside it
/// for ENG-UK - and the editor simply drops whichever one the active layout does not define. The
/// arrangement itself is a 17-inch chassis with a numpad; it is drawn from the key names the
/// recovered maps contain and has not been photographed against hardware, so treat the shape as
/// approximate. What is not approximate is the name each drawn key carries, and that is all the
/// colour mapping depends on.
/// </para>
/// </remarks>
public static class KeyboardGeometry
{
    /// <summary>Pixels per key-width. One unit is a letter key.</summary>
    public const double UnitPixels = 18.0;

    /// <summary>Gap left around every key, in pixels, inside its unit width.</summary>
    public const double KeyMargin = 1.0;

    private static readonly KeyBlockPlacement Main = new("Main", 0, new KeyRowPlacement[]
    {
        Row("ESC", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"),
        Row(new KeyPlacement("~"), K("1"), K("2"), K("3"), K("4"), K("5"), K("6"), K("7"), K("8"),
            K("9"), K("0"), K("-"), K("="), new KeyPlacement("Backspace", 2.0)),
        Row(new KeyPlacement("Tab", 1.5), K("Q"), K("W"), K("E"), K("R"), K("T"), K("Y"), K("U"),
            K("I"), K("O"), K("P"), K("["), K("]"), new KeyPlacement("\\", 1.5)),
        // "#" is the ENG-UK layout's extra key and is absent from ENG-US, where Enter simply
        // starts one unit earlier. Both are listed; the editor keeps whichever one resolves.
        Row(new KeyPlacement("Caps", 1.75), K("A"), K("S"), K("D"), K("F"), K("G"), K("H"), K("J"),
            K("K"), K("L"), K(";"), K("'"), K("#"), new KeyPlacement("Enter", 2.25)),
        Row(new KeyPlacement("Shift-L", 2.25), K("Z"), K("X"), K("C"), K("V"), K("B"), K("N"),
            K("M"), K(","), K("."), K("/"), new KeyPlacement("Shift-R", 2.75)),
        Row(new KeyPlacement("Ctrl-L", 1.25), K("Fn"), K("WinKey"), new KeyPlacement("Alt-L", 1.25),
            new KeyPlacement("Space", 6.25), new KeyPlacement("Alt-R", 1.25), K("Menu"),
            new KeyPlacement("Ctrl-R", 2.0)),
    });

    private static readonly KeyBlockPlacement Arrows = new("Arrows", 0.5, new[]
    {
        new KeyRowPlacement(1.0, new[] { K("Up") }),
        new KeyRowPlacement(0.0, new[] { K("Left"), K("Down"), K("Right") }),
    });

    private static readonly KeyBlockPlacement Navigation = new("Navigation", 0.5, new[]
    {
        Row(new KeyPlacement("Del", 1.5)),
        Row(new KeyPlacement("Pause", 1.5)),
        Row(new KeyPlacement("Home", 1.5)),
        Row(new KeyPlacement("PgUp", 1.5)),
        Row(new KeyPlacement("PgDn", 1.5)),
        Row(new KeyPlacement("End", 1.5)),
    });

    private static readonly KeyBlockPlacement NumPad = new("Numpad", 0.5, new[]
    {
        Row("NumLk", "Num-/", "Num-*", "Num--"),
        Row("Num-7", "Num-8", "Num-9", "Num-+"),
        Row("Num-4", "Num-5", "Num-6"),
        Row("Num-1", "Num-2", "Num-3", "Num-Enter"),
        Row(new KeyPlacement("Num-0", 2.0), K("Num-.")),
    });

    /// <summary>The picture, left to right: main block, arrows, navigation column, numpad.</summary>
    public static IReadOnlyList<KeyBlockPlacement> Blocks { get; } = new[] { Main, Arrows, Navigation, NumPad };

    /// <summary>
    /// The short label drawn on a key. The full name is what the tooltip and the mapping use;
    /// a key one unit wide has room for about three characters and no more.
    /// </summary>
    public static string CaptionFor(string name) => name switch
    {
        "ESC" => "Esc",
        "Backspace" => "Bksp",
        "Enter" or "Num-Enter" => "Ent",
        "Shift-L" or "Shift-R" => "Shift",
        "Ctrl-L" or "Ctrl-R" => "Ctrl",
        "Alt-L" or "Alt-R" => "Alt",
        "WinKey" => "Win",
        "Menu" => "☰",
        "NumLk" => "NLk",
        "Pause" => "Paus",
        "Up" => "↑",
        "Down" => "↓",
        "Left" => "←",
        "Right" => "→",
        // The numpad's names are prefixed only to keep them distinct from the main block's;
        // on the picture they sit in their own cluster and the prefix is just noise.
        _ => name.StartsWith("Num-", StringComparison.Ordinal) ? name["Num-".Length..] : name,
    };

    private static KeyPlacement K(string name) => new(name);

    private static KeyRowPlacement Row(params string[] names) =>
        new(0.0, Array.ConvertAll(names, K));

    private static KeyRowPlacement Row(params KeyPlacement[] keys) => new(0.0, keys);
}
