using System.Globalization;
using MediaColor = System.Windows.Media.Color;

namespace OpenAorus.App;

/// <summary>
/// The colour picker, such as it is: a fixed row of swatches and a hex box.
/// </summary>
/// <remarks>
/// WPF has no colour picker of its own and the alternative is a NuGet package several times the
/// size of this app, which would undo the point of replacing Gigabyte's. A dozen swatches cover
/// what most owners want and the hex box covers the rest, so this is the whole picker.
/// </remarks>
public static class ColorText
{
    private const int HexDigits = 6;

    /// <summary>The offered colours: the primaries and secondaries, a warm white, and the app's accent.</summary>
    public static IReadOnlyList<MediaColor> Swatches { get; } = new[]
    {
        MediaColor.FromRgb(0xFF, 0xFF, 0xFF),
        MediaColor.FromRgb(0xFF, 0xC8, 0x80),
        MediaColor.FromRgb(0xFF, 0x7A, 0x1A),
        MediaColor.FromRgb(0xFF, 0x00, 0x00),
        MediaColor.FromRgb(0xFF, 0x00, 0x80),
        MediaColor.FromRgb(0xFF, 0x00, 0xFF),
        MediaColor.FromRgb(0x80, 0x00, 0xFF),
        MediaColor.FromRgb(0x00, 0x00, 0xFF),
        MediaColor.FromRgb(0x00, 0x80, 0xFF),
        MediaColor.FromRgb(0x00, 0xFF, 0xFF),
        MediaColor.FromRgb(0x00, 0xFF, 0x00),
        MediaColor.FromRgb(0xFF, 0xFF, 0x00),
        MediaColor.FromRgb(0x00, 0x00, 0x00),
    };

    /// <summary>
    /// Whether text on this colour should be light. Uses the perceived-brightness weighting
    /// rather than a plain average, because a saturated green reads far brighter than a blue
    /// of the same nominal value and the editor draws keys in whatever colour they will light.
    /// </summary>
    public static bool IsDark(MediaColor color) =>
        (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B) < 128.0;

    /// <summary>A colour as <c>#RRGGBB</c>, which is what <see cref="TryParse"/> reads back.</summary>
    public static string Format(MediaColor color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Reads <c>#RRGGBB</c> or <c>RRGGBB</c>, in either case, with surrounding whitespace allowed.
    /// Anything else fails rather than being guessed at: the box is bound live, so it sees every
    /// half-typed value on the way to a complete one and must leave the keyboard alone until then.
    /// </summary>
    public static bool TryParse(string? text, out MediaColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#') span = span[1..];
        if (span.Length != HexDigits) return false;

        // HexNumber alone would accept a leading sign or whitespace inside the six characters,
        // both of which have already been ruled out by the length check above.
        foreach (var c in span)
            if (!Uri.IsHexDigit(c)) return false;

        var value = uint.Parse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        color = MediaColor.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }
}
