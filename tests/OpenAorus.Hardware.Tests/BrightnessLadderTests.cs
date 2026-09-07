using OpenAorus.Hardware.Display;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Where one press of a brightness key lands, given the levels the panel says it will accept.
/// </summary>
/// <remarks>
/// Pure and total, for the reason <see cref="OpenAorus.Hardware.Hotkeys.RawInputDecoder"/> is: it
/// runs on a callback path, and the list it is handed comes out of a WMI provider rather than out
/// of this app. An empty list, an unsorted one, duplicates, a current level that is not on the
/// ladder at all - all of those are things a panel can present, and none of them may throw.
/// </remarks>
public class BrightnessLadderTests
{
    private static IReadOnlyList<int> Dense() => Enumerable.Range(0, 101).ToArray();

    // ---- the ordinary case -----------------------------------------------------------------

    [Theory]
    [InlineData(50, 60)]
    [InlineData(0, 10)]
    [InlineData(37, 47)]
    public void On_a_dense_ladder_one_press_moves_a_tenth_of_it(int current, int expected)
    {
        // 101 levels, a stride of ten of them, so a dense 0-100 panel moves ten points a press -
        // which is what Windows' own brightness key does and what the owner is comparing against.
        Assert.Equal(expected, BrightnessLadder.Next(Dense(), current, BrightnessLadder.Up));
    }

    [Theory]
    [InlineData(50, 40)]
    [InlineData(100, 90)]
    [InlineData(37, 27)]
    public void Down_moves_the_same_distance_the_other_way(int current, int expected)
    {
        Assert.Equal(expected, BrightnessLadder.Next(Dense(), current, BrightnessLadder.Down));
    }

    // ---- sparse ladders --------------------------------------------------------------------

    [Fact]
    public void A_sparse_ladder_moves_one_of_its_own_levels_rather_than_ten_points()
    {
        // THE REASON THIS IS A LADDER AND NOT ARITHMETIC. A panel that accepts only these six
        // values would refuse 30, and adding ten to 20 would either fail or be silently rounded
        // by the firmware to something the app did not choose.
        var sparse = new[] { 0, 20, 40, 60, 80, 100 };

        Assert.Equal(40, BrightnessLadder.Next(sparse, 20, BrightnessLadder.Up));
        Assert.Equal(0, BrightnessLadder.Next(sparse, 20, BrightnessLadder.Down));
    }

    [Fact]
    public void A_very_sparse_ladder_still_moves_exactly_one_level()
    {
        var coarse = new[] { 0, 50, 100 };

        Assert.Equal(50, BrightnessLadder.Next(coarse, 0, BrightnessLadder.Up));
        Assert.Equal(100, BrightnessLadder.Next(coarse, 50, BrightnessLadder.Up));
        Assert.Equal(50, BrightnessLadder.Next(coarse, 100, BrightnessLadder.Down));
    }

    [Fact]
    public void A_ladder_of_two_moves_between_its_two_levels()
    {
        var pair = new[] { 0, 100 };

        Assert.Equal(100, BrightnessLadder.Next(pair, 0, BrightnessLadder.Up));
        Assert.Equal(0, BrightnessLadder.Next(pair, 100, BrightnessLadder.Down));
    }

    [Fact]
    public void The_stride_is_a_fraction_of_the_ladder_and_never_a_fraction_of_a_level()
    {
        // A ladder of eleven - 0, 10, 20 ... 100 - is a tenth of eleven, which rounds to nothing.
        // Rounding down to zero would be a key that does nothing at all, so the floor is one.
        var tens = new[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

        Assert.Equal(60, BrightnessLadder.Next(tens, 50, BrightnessLadder.Up));
        Assert.Equal(40, BrightnessLadder.Next(tens, 50, BrightnessLadder.Down));
    }

    // ---- the ends ---------------------------------------------------------------------------

    [Fact]
    public void Pressing_up_at_the_top_stays_at_the_top()
    {
        // Not null and not an error: the panel really is at full, and saying so is what lets the
        // overlay redraw honestly on a key the owner is holding at the end of its travel.
        Assert.Equal(100, BrightnessLadder.Next(Dense(), 100, BrightnessLadder.Up));
        Assert.Equal(100, BrightnessLadder.Next(new[] { 0, 50, 100 }, 100, BrightnessLadder.Up));
    }

    [Fact]
    public void Pressing_down_at_the_bottom_stays_at_the_bottom()
    {
        Assert.Equal(0, BrightnessLadder.Next(Dense(), 0, BrightnessLadder.Down));
        Assert.Equal(0, BrightnessLadder.Next(new[] { 0, 50, 100 }, 0, BrightnessLadder.Down));
    }

    [Fact]
    public void A_stride_that_would_overshoot_the_end_lands_on_the_end()
    {
        // 95 up by ten points on a dense ladder is 105, which no panel accepts.
        Assert.Equal(100, BrightnessLadder.Next(Dense(), 95, BrightnessLadder.Up));
        Assert.Equal(0, BrightnessLadder.Next(Dense(), 5, BrightnessLadder.Down));
    }

    [Fact]
    public void A_ladder_whose_bottom_is_not_zero_clamps_to_its_own_bottom()
    {
        // Panels that refuse to go fully dark report a floor of their own. Clamping to zero
        // would be this app inventing a level the panel never offered.
        var floored = new[] { 20, 40, 60, 80, 100 };

        Assert.Equal(20, BrightnessLadder.Next(floored, 20, BrightnessLadder.Down));
        Assert.Equal(100, BrightnessLadder.Next(floored, 100, BrightnessLadder.Up));
    }

    // ---- what a provider can hand over ------------------------------------------------------

    [Fact]
    public void A_current_level_that_is_not_on_the_ladder_still_moves_one_level()
    {
        // The panel can report a level it will not accept back - a level set by something else,
        // or a firmware that rounds. Both directions have to move off it rather than stall.
        var sparse = new[] { 0, 50, 100 };

        Assert.Equal(100, BrightnessLadder.Next(sparse, 55, BrightnessLadder.Up));
        Assert.Equal(50, BrightnessLadder.Next(sparse, 55, BrightnessLadder.Down));
    }

    [Fact]
    public void A_current_level_outside_the_ladder_entirely_lands_inside_it()
    {
        var sparse = new[] { 20, 50, 80 };

        Assert.Equal(20, BrightnessLadder.Next(sparse, 5, BrightnessLadder.Up));
        Assert.Equal(80, BrightnessLadder.Next(sparse, 200, BrightnessLadder.Down));
        Assert.Equal(20, BrightnessLadder.Next(sparse, -30, BrightnessLadder.Up));
    }

    [Fact]
    public void An_unsorted_ladder_is_read_in_order_rather_than_as_given()
    {
        // The WMI array is documented ascending. "Documented" is not "checked", and a descending
        // one read literally would invert both keys.
        var jumbled = new[] { 100, 0, 50 };

        Assert.Equal(50, BrightnessLadder.Next(jumbled, 0, BrightnessLadder.Up));
        Assert.Equal(50, BrightnessLadder.Next(jumbled, 100, BrightnessLadder.Down));
    }

    [Fact]
    public void Duplicates_do_not_cost_a_press()
    {
        // A ladder listing 50 twice must not make one press move nowhere.
        var repeated = new[] { 0, 50, 50, 100 };

        Assert.Equal(50, BrightnessLadder.Next(repeated, 0, BrightnessLadder.Up));
        Assert.Equal(100, BrightnessLadder.Next(repeated, 50, BrightnessLadder.Up));
    }

    [Fact]
    public void A_panel_with_no_levels_at_all_has_nowhere_to_go()
    {
        // Null, not zero. Zero is a level; this is the absence of one, and the difference is
        // whether the app writes darkness to a panel that never offered a ladder.
        Assert.Null(BrightnessLadder.Next(Array.Empty<int>(), 50, BrightnessLadder.Up));
        Assert.Null(BrightnessLadder.Next(Array.Empty<int>(), 50, BrightnessLadder.Down));
    }

    [Fact]
    public void A_ladder_of_one_level_is_already_at_both_ends()
    {
        var single = new[] { 40 };

        Assert.Equal(40, BrightnessLadder.Next(single, 40, BrightnessLadder.Up));
        Assert.Equal(40, BrightnessLadder.Next(single, 40, BrightnessLadder.Down));
    }

    [Fact]
    public void A_direction_of_nothing_moves_nothing()
    {
        Assert.Null(BrightnessLadder.Next(Dense(), 50, 0));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-7)]
    public void Any_positive_is_up_and_any_negative_is_down(int direction)
    {
        // The policy passes 1 and -1. Reading the sign rather than the number means a caller that
        // passes something else cannot land the panel somewhere nobody chose.
        var expected = direction > 0 ? 60 : 40;
        Assert.Equal(expected, BrightnessLadder.Next(Dense(), 50, direction));
    }

    [Fact]
    public void A_null_ladder_is_this_app_calling_itself_wrongly()
    {
        Assert.Throws<ArgumentNullException>(() => BrightnessLadder.Next(null!, 50, BrightnessLadder.Up));
    }

    [Fact]
    public void Nothing_a_panel_can_report_makes_this_throw()
    {
        // It runs on a callback path. Every combination of a small ladder and a wild current
        // level, walked rather than sampled.
        var ladders = new[]
        {
            Array.Empty<int>(),
            new[] { 0 },
            new[] { 0, 100 },
            new[] { 100, 0 },
            new[] { 0, 0, 0 },
            new[] { -5, 0, 5 },
            new[] { int.MinValue, 0, int.MaxValue },
        };

        foreach (var ladder in ladders)
        foreach (var current in new[] { int.MinValue, -1, 0, 50, 100, int.MaxValue })
        foreach (var direction in new[] { -1, 0, 1 })
        {
            var error = Record.Exception(() => BrightnessLadder.Next(ladder, current, direction));
            Assert.Null(error);
        }
    }

    [Fact]
    public void Whatever_comes_back_is_a_level_the_panel_offered()
    {
        // The one property that must hold for every ladder there is: this never invents a value.
        var ladders = new[]
        {
            new[] { 0, 100 },
            new[] { 0, 20, 40, 60, 80, 100 },
            new[] { 20, 50, 80 },
            Enumerable.Range(0, 101).ToArray(),
        };

        foreach (var ladder in ladders)
        foreach (var current in new[] { -10, 0, 1, 19, 50, 99, 100, 250 })
        foreach (var direction in new[] { BrightnessLadder.Down, BrightnessLadder.Up })
        {
            var next = BrightnessLadder.Next(ladder, current, direction);
            Assert.NotNull(next);
            Assert.Contains(next!.Value, ladder);
        }
    }
}
