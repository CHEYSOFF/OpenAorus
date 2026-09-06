using OpenAorus.App;
using MediaColor = System.Windows.Media.Color;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// WPF ships no colour picker and the app is deliberately too small to take a dependency on
/// one, so the panel offers a row of swatches and a hex box. The hex box is the half that can
/// be handed nonsense.
/// </summary>
public class ColorTextTests
{
    [Theory]
    [InlineData("#FF7A1A")]
    [InlineData("FF7A1A")]
    [InlineData("#ff7a1a")]
    [InlineData("  #FF7A1A  ")]
    public void A_six_digit_hex_parses_however_it_is_written(string text)
    {
        Assert.True(ColorText.TryParse(text, out var color));
        Assert.Equal(MediaColor.FromRgb(0xFF, 0x7A, 0x1A), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("#FF7A1")]
    [InlineData("#FF7A1AA")]
    [InlineData("#GG7A1A")]
    [InlineData("orange")]
    [InlineData("#FF7A1A;DROP")]
    public void Anything_else_is_refused_rather_than_guessed(string? text)
    {
        Assert.False(ColorText.TryParse(text, out _));
    }

    [Fact]
    public void Formatting_round_trips_through_parsing()
    {
        var color = MediaColor.FromRgb(0x01, 0x80, 0xFE);

        var text = ColorText.Format(color);

        Assert.Equal("#0180FE", text);
        Assert.True(ColorText.TryParse(text, out var back));
        Assert.Equal(color, back);
    }

    /// <summary>
    /// A key in the editor is drawn in the colour it will light, and its caption has to stay
    /// readable on top of that - including on the black an unpainted key starts as.
    /// </summary>
    [Theory]
    [InlineData(0x00, 0x00, 0x00, true)]
    [InlineData(0xFF, 0xFF, 0xFF, false)]
    [InlineData(0x00, 0xFF, 0x00, false)]   // green carries most of the perceived brightness
    [InlineData(0x00, 0x00, 0xFF, true)]    // blue carries least
    [InlineData(0xFF, 0x7A, 0x1A, false)]
    public void A_colour_knows_whether_it_needs_light_text(byte r, byte g, byte b, bool dark)
    {
        Assert.Equal(dark, ColorText.IsDark(MediaColor.FromRgb(r, g, b)));
    }

    /// <summary>
    /// The swatches are the picker for anyone who does not think in hex, so an empty or
    /// duplicate-ridden list would quietly make the panel useless.
    /// </summary>
    [Fact]
    public void The_swatches_are_a_dozen_distinct_colours()
    {
        Assert.InRange(ColorText.Swatches.Count, 8, 20);
        Assert.Equal(ColorText.Swatches.Count, ColorText.Swatches.Distinct().Count());
        Assert.Contains(MediaColor.FromRgb(0xFF, 0x7A, 0x1A), ColorText.Swatches); // the app's accent
    }
}
