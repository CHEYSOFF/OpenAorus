using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class KeyLayoutTests
{
    [Theory]
    [InlineData(KeyboardLayout.EngUs)]
    [InlineData(KeyboardLayout.EngUk)]
    public void Every_layout_has_exactly_128_slots(KeyboardLayout layout)
        => Assert.Equal(128, KeyLayout.For(layout).Slots.Count);

    [Theory]
    [InlineData(KeyboardLayout.EngUs)]
    [InlineData(KeyboardLayout.EngUk)]
    public void Real_key_names_are_unique_within_a_layout(KeyboardLayout layout)
    {
        var names = KeyLayout.For(layout).RealKeys.Select(k => k.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(KeyboardLayout.EngUs)]
    [InlineData(KeyboardLayout.EngUk)]
    public void A_layout_populates_around_a_hundred_real_keys(KeyboardLayout layout)
        => Assert.InRange(KeyLayout.For(layout).RealKeys.Count(), 95, 110);

    [Fact]
    public void Known_slots_match_the_recovered_order()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs);
        Assert.Equal("Ctrl-R", us.NameAt(4));
        Assert.Equal("Q", us.NameAt(8));
        Assert.Equal("ESC", us.NameAt(11));
        Assert.Equal(KeyLayout.Unused, us.NameAt(0));
        Assert.Equal(8, us.IndexOf("Q"));
    }

    [Fact]
    public void IndexOf_returns_minus_one_for_an_unknown_or_unused_name()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs);
        Assert.Equal(-1, us.IndexOf("NoSuchKey"));
        Assert.Equal(-1, us.IndexOf(KeyLayout.Unused));
    }

    [Fact]
    public void The_two_layouts_are_not_identical()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs).Slots;
        var uk = KeyLayout.For(KeyboardLayout.EngUk).Slots;
        Assert.NotEqual(us, uk);
    }

    [Fact]
    public void Product_id_7A3D_defaults_to_the_UK_order_and_7A3C_to_US()
    {
        Assert.Equal(KeyboardLayout.EngUk, KeyLayout.ForProduct(0x7A3D).Layout);
        Assert.Equal(KeyboardLayout.EngUs, KeyLayout.ForProduct(0x7A3C).Layout);
    }

    [Fact]
    public void NameAt_rejects_a_slot_outside_the_report()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs);
        Assert.Throws<ArgumentOutOfRangeException>(() => us.NameAt(128));
        Assert.Throws<ArgumentOutOfRangeException>(() => us.NameAt(-1));
    }

    // The transcription guards below pin the recovered slot orders against
    // docs/research/ione-keymap-eng-{us,uk}.txt. Nothing verifies these on hardware yet,
    // so an edit that "tidies" a name has to break a test.

    [Fact]
    public void The_two_layouts_differ_only_in_the_hash_and_backslash_slots()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs);
        var uk = KeyLayout.For(KeyboardLayout.EngUk);
        var differing = Enumerable.Range(0, KeyLayout.SlotCount)
            .Where(i => us.NameAt(i) != uk.NameAt(i))
            .ToList();
        Assert.Equal(new[] { 68, 82 }, differing);

        Assert.Equal(KeyLayout.Unused, us.NameAt(68));
        Assert.Equal("#", uk.NameAt(68));
        Assert.Equal("\\", us.NameAt(82));
        Assert.Equal(KeyLayout.Unused, uk.NameAt(82));
    }

    [Fact]
    public void The_US_row_boundaries_match_the_recovered_file()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs);
        // First and last entry of each eight-name line in ione-keymap-eng-us.txt.
        var expected = new[]
        {
            (0, "N/A"), (7, "F5"),
            (8, "Q"), (15, "1"),
            (16, "W"), (23, "2"),
            (24, "E"), (31, "3"),
            (32, "R"), (39, "4"),
            (40, "U"), (47, "7"),
            (48, "I"), (55, "8"),
            (56, "O"), (63, "9"),
            (64, "P"), (71, "0"),
            (72, "N/A"), (79, "Pause"),
            (80, "N/A"), (87, "F10"),
            (88, "Num-7"), (95, "N/A"),
            (96, "Num-8"), (103, "Del"),
            (104, "Num-9"), (111, "PgDn"),
            (112, "Num-+"), (119, "End"),
            (120, "N/A"), (127, "N/A"),
        };
        foreach (var (slot, name) in expected)
            Assert.Equal(name, us.NameAt(slot));
    }

    [Fact]
    public void Bracket_and_punctuation_slots_are_transcribed_as_recovered()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs);
        // ']' sits in the I column and '[' in the P column, which is the reverse of the
        // physical order. That is what the recovered order says; do not swap them.
        Assert.Equal("]", us.NameAt(49));
        Assert.Equal("[", us.NameAt(65));
        Assert.Equal("=", us.NameAt(54));
        Assert.Equal("-", us.NameAt(70));
        Assert.Equal("~", us.NameAt(14));
        Assert.Equal("'", us.NameAt(67));
    }

    [Theory]
    [InlineData(KeyboardLayout.EngUs)]
    [InlineData(KeyboardLayout.EngUk)]
    public void Every_slot_holds_a_non_empty_name(KeyboardLayout layout)
        => Assert.All(KeyLayout.For(layout).Slots, name => Assert.False(string.IsNullOrWhiteSpace(name)));

    [Theory]
    [InlineData(KeyboardLayout.EngUs)]
    [InlineData(KeyboardLayout.EngUk)]
    public void RealKeys_reports_the_slot_its_name_lives_at(KeyboardLayout layout)
    {
        var map = KeyLayout.For(layout);
        foreach (var (slot, name) in map.RealKeys)
        {
            Assert.Equal(name, map.NameAt(slot));
            Assert.Equal(slot, map.IndexOf(name));
        }
    }
}
