namespace OpenAorus.Hardware.Display;

/// <summary>
/// Where one press of a brightness key lands, given the levels the panel says it will accept.
/// </summary>
/// <remarks>
/// <para>
/// STEPPING A LADDER, NOT ADDING A PERCENTAGE. A panel reports the levels it supports, and that
/// list is not always <c>0..100</c>: some report a handful of coarse stops, some a floor below
/// which they will not go dark. Adding a fixed ten points to the current level would produce values
/// such a panel refuses outright, or that its firmware silently rounds to something this app did
/// not choose - and the app would then draw an overlay saying a number the screen is not showing.
/// So every answer here is a member of the list it was given, and
/// <c>Whatever_comes_back_is_a_level_the_panel_offered</c> is the test that says so.
/// </para>
/// <para>
/// HOW FAR ONE PRESS GOES, and why it is a fraction of the ladder rather than one entry of it. One
/// entry is the obvious rule and it is wrong on the common case: most laptop panels report all 101
/// levels from 0 to 100, and one entry there is one percentage point - about a hundred presses to
/// cross the range, against the ten Windows' own brightness key takes. So the stride is
/// <see cref="StepsPerPress"/>-th of the ladder, rounded down, and never less than one entry. On a
/// dense 0-100 ladder that is ten points a press, which is what the owner is comparing against; on
/// a ladder of six stops it is one stop; on a ladder of three it is one. It scales with the panel
/// instead of assuming its shape, and it can never overshoot a short ladder's far end because the
/// index is clamped rather than the value.
/// </para>
/// <para>
/// Pure and total, for the reason <see cref="Hotkeys.RawInputDecoder"/> is. The list comes from a
/// WMI provider rather than from this app, so an empty one, an unsorted one, duplicates and a
/// current level nowhere on the ladder are all things that can arrive, and none of them may throw
/// on a path reached from a window procedure. Nothing here does arithmetic on a level either -
/// only comparisons and indexing - so a provider reporting <see cref="int.MaxValue"/> cannot
/// overflow it.
/// </para>
/// </remarks>
public static class BrightnessLadder
{
    /// <summary>The direction of the brightness-up key.</summary>
    public const int Up = 1;

    /// <summary>The direction of the brightness-down key.</summary>
    public const int Down = -1;

    /// <summary>How many presses cross the whole ladder, when the ladder is long enough to have
    /// that many steps in it.</summary>
    /// <remarks>Ten, because that is what Windows' own brightness key does on a dense panel and
    /// this feature is judged against it. On a ladder shorter than this the floor of one entry
    /// takes over and a press moves one stop instead.</remarks>
    public const int StepsPerPress = 10;

    /// <summary>The level one press in <paramref name="direction"/> lands on.</summary>
    /// <param name="levels">The levels the panel accepts, in any order, duplicates allowed.</param>
    /// <param name="current">The level the panel reports now, on the ladder or not.</param>
    /// <param name="direction">Positive for up, negative for down; zero moves nothing.</param>
    /// <returns>A level from <paramref name="levels"/> - the current one if the press was at the
    /// end of the ladder's travel - or null if there is no ladder to step or no direction to
    /// step in.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="levels"/> is null.</exception>
    public static int? Next(IReadOnlyList<int> levels, int current, int direction)
    {
        // A null list is this app calling itself wrongly, not something a panel can report: an
        // implementation with nothing to say returns a null state, which never reaches here.
        ArgumentNullException.ThrowIfNull(levels);

        if (direction == 0) return null;

        // Sorted and deduplicated here rather than trusted from the provider. The WMI array is
        // documented ascending, which is a reading of the documentation; a descending one taken
        // literally would invert both keys, and a repeated level would cost a press.
        var ladder = levels.Distinct().OrderBy(l => l).ToArray();
        if (ladder.Length == 0) return null;

        var stride = Math.Max(1, ladder.Length / StepsPerPress);

        // The anchor is chosen so that the very next index in the direction of travel is always
        // strictly past the current level, whether or not the current level is on the ladder.
        // Going up that means the LAST entry at or below it, and going down the FIRST entry at or
        // above it; a current level off either end anchors just outside, so the first press lands
        // inside the ladder rather than stalling on a level the panel would not accept back.
        // The sign, not the number. The policy passes 1 and -1; reading the magnitude would let a
        // caller that passed something else move the panel somewhere nobody chose.
        var index = direction > 0
            ? LastAtOrBelow(ladder, current) + stride
            : FirstAtOrAbove(ladder, current) - stride;

        // Clamped on the index, not on the value: a stride that runs off the end lands on the end
        // itself, which is a level the panel offered, rather than on a number computed past it.
        return ladder[Math.Clamp(index, 0, ladder.Length - 1)];
    }

    /// <summary>The index of the last entry at or below <paramref name="current"/>, or -1.</summary>
    private static int LastAtOrBelow(int[] ladder, int current)
    {
        var found = -1;
        for (var i = 0; i < ladder.Length; i++)
        {
            if (ladder[i] > current) break;
            found = i;
        }
        return found;
    }

    /// <summary>The index of the first entry at or above <paramref name="current"/>, or the
    /// length.</summary>
    private static int FirstAtOrAbove(int[] ladder, int current)
    {
        for (var i = 0; i < ladder.Length; i++)
            if (ladder[i] >= current) return i;
        return ladder.Length;
    }
}
