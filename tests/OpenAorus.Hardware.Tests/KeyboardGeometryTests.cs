using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The picture and the wire are two different orders, and these are what keeps them apart.
/// <see cref="KeyboardGeometry"/> says where a key is drawn; <see cref="KeyLayout"/> says which
/// of the 128 slots lights it. The only thing joining them is the key's name, so the tests that
/// matter are the ones asserting that every drawn name resolves, that it resolves to a distinct
/// slot, and that the drawn order is emphatically not the slot order.
/// </summary>
public class KeyboardGeometryTests
{
    private static IEnumerable<string> DrawnNames =>
        KeyboardGeometry.Blocks.SelectMany(b => b.Rows).SelectMany(r => r.Keys).Select(k => k.Name);

    [Theory]
    [InlineData(KeyboardLayout.EngUs)]
    [InlineData(KeyboardLayout.EngUk)]
    public void Every_populated_slot_is_drawn_exactly_once(KeyboardLayout which)
    {
        var layout = KeyLayout.For(which);

        var drawn = DrawnNames
            .Select(name => (Name: name, Slot: layout.IndexOf(name)))
            .Where(k => k.Slot >= 0)
            .ToList();

        Assert.Equal(drawn.Count, drawn.Select(k => k.Slot).Distinct().Count());
        Assert.Equal(
            layout.RealKeys.Select(k => k.Slot).OrderBy(s => s),
            drawn.Select(k => k.Slot).OrderBy(s => s));
    }

    /// <summary>
    /// The two orders differ only at the backslash and the hash, so a name drawn on neither
    /// keyboard is a typo in the picture that would silently render an unpaintable key.
    /// </summary>
    [Fact]
    public void No_key_is_drawn_that_neither_layout_defines()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs);
        var uk = KeyLayout.For(KeyboardLayout.EngUk);

        var orphans = DrawnNames.Where(n => us.IndexOf(n) < 0 && uk.IndexOf(n) < 0).ToList();

        Assert.Empty(orphans);
    }

    [Fact]
    public void A_key_is_drawn_only_once_across_the_whole_picture()
    {
        var names = DrawnNames.ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The slots are an electrical scan matrix, so pairs that sit next to each other on the
    /// keyboard are far apart and out of order in the report - the brackets are the clearest
    /// case, and the minus and equals are the second. Anyone tempted to "fix" the slot arrays
    /// so the picture reads left to right breaks the lighting on every key; this fails first.
    /// </summary>
    [Fact]
    public void The_drawn_order_is_not_the_slot_order()
    {
        var layout = KeyLayout.For(KeyboardLayout.EngUs);
        var drawn = DrawnNames.ToList();

        Assert.True(drawn.IndexOf("[") < drawn.IndexOf("]"), "the picture draws [ before ]");
        Assert.True(layout.IndexOf("]") < layout.IndexOf("["), "the report carries ] before [");

        Assert.True(drawn.IndexOf("-") < drawn.IndexOf("="), "the picture draws - before =");
        Assert.True(layout.IndexOf("=") < layout.IndexOf("-"), "the report carries = before -");
    }

    [Fact]
    public void Every_key_has_a_positive_width_and_a_caption()
    {
        foreach (var block in KeyboardGeometry.Blocks)
        {
            Assert.NotEmpty(block.Rows);
            Assert.True(block.IndentUnits >= 0);
            foreach (var row in block.Rows)
            {
                Assert.True(row.IndentUnits >= 0);
                foreach (var key in row.Keys)
                {
                    Assert.True(key.Units > 0, $"{key.Name} has width {key.Units}");
                    Assert.False(string.IsNullOrWhiteSpace(KeyboardGeometry.CaptionFor(key.Name)));
                }
            }
        }
    }
}
