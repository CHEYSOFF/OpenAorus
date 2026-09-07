using OpenAorus.Hardware.Display;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// One press of a brightness key, from reading the panel to writing it back.
/// </summary>
/// <remarks>
/// <para>
/// The firmware on this chassis reports the brightness keys and then does nothing about them - see
/// the "Observed on hardware" section of <c>docs/research/fn-hotkey-signals.md</c> - so this is the
/// only thing that makes those keys work. Every question about it is answered against a fake,
/// because the alternative is a bench with a screen someone has to watch.
/// </para>
/// <para>
/// The contract that matters most is the quiet one: this runs on a raw-input callback, and a
/// desktop, an external monitor or a WMI provider having a bad day must all come back as "nothing
/// happened" rather than as an exception on a window procedure.
/// </para>
/// </remarks>
public class PanelBrightnessControllerTests
{
    private static PanelBrightnessController Over(FakePanelBrightness panel) => new(panel);

    [Fact]
    public void One_press_up_reads_the_panel_and_writes_the_next_level_back()
    {
        var panel = new FakePanelBrightness { State = new PanelBrightnessState(50, FakePanelBrightness.DenseLadder) };

        var level = Over(panel).Step(BrightnessLadder.Up);

        Assert.Equal(60, level);
        Assert.Equal(new[] { 60 }, panel.Written);
        // Read first, every time. Something else on the machine can move the panel - Windows'
        // own slider, the power policy on battery - and a cached level would step from a value
        // the screen stopped showing minutes ago.
        Assert.Equal(1, panel.Reads);
    }

    [Fact]
    public void One_press_down_writes_the_level_below()
    {
        var panel = new FakePanelBrightness { State = new PanelBrightnessState(50, FakePanelBrightness.DenseLadder) };

        Assert.Equal(40, Over(panel).Step(BrightnessLadder.Down));
        Assert.Equal(new[] { 40 }, panel.Written);
    }

    [Fact]
    public void Presses_walk_the_ladder_rather_than_repeating_one_step()
    {
        var panel = new FakePanelBrightness { State = new PanelBrightnessState(0, new[] { 0, 50, 100 }) };
        var controller = Over(panel);

        Assert.Equal(50, controller.Step(BrightnessLadder.Up));
        Assert.Equal(100, controller.Step(BrightnessLadder.Up));
        Assert.Equal(new[] { 50, 100 }, panel.Written);
    }

    [Fact]
    public void At_the_top_the_level_is_reported_and_nothing_is_written()
    {
        var panel = new FakePanelBrightness { State = new PanelBrightnessState(100, FakePanelBrightness.DenseLadder) };

        // The panel really is at 100, so the caller gets 100 and the overlay can say so - the key
        // is not broken, it is at the end of its travel. But there is nothing to write, and a
        // held key at the top must not be a write per repeat.
        Assert.Equal(100, Over(panel).Step(BrightnessLadder.Up));
        Assert.Empty(panel.Written);
    }

    [Fact]
    public void At_the_bottom_the_same_thing_happens_the_other_way()
    {
        var panel = new FakePanelBrightness { State = new PanelBrightnessState(0, FakePanelBrightness.DenseLadder) };

        Assert.Equal(0, Over(panel).Step(BrightnessLadder.Down));
        Assert.Empty(panel.Written);
    }

    // ---- degrading quietly -------------------------------------------------------------------

    [Fact]
    public void A_machine_with_no_controllable_panel_does_nothing_and_says_so()
    {
        // A desktop, or a laptop lid closed onto an external monitor: WmiMonitorBrightness has no
        // instance. Null is the whole answer - no write, no exception, no overlay.
        var panel = new FakePanelBrightness { State = null };

        Assert.Null(Over(panel).Step(BrightnessLadder.Up));
        Assert.Empty(panel.Written);
    }

    [Fact]
    public void A_panel_that_offers_no_levels_is_the_same_as_no_panel()
    {
        var panel = new FakePanelBrightness { State = new PanelBrightnessState(50, Array.Empty<int>()) };

        Assert.Null(Over(panel).Step(BrightnessLadder.Up));
        Assert.Empty(panel.Written);
    }

    [Fact]
    public void A_write_the_panel_refuses_is_not_reported_as_a_change()
    {
        var panel = new FakePanelBrightness
        {
            State = new PanelBrightnessState(50, FakePanelBrightness.DenseLadder),
            FailNextSet = true,
        };

        // It was attempted - that is the difference between this and no panel at all - but the
        // screen did not move, so an overlay claiming 60 % would be the app narrating a change
        // that never happened.
        Assert.Null(Over(panel).Step(BrightnessLadder.Up));
        Assert.Equal(new[] { 60 }, panel.Written);
    }

    [Fact]
    public void A_provider_that_throws_on_the_read_is_treated_as_no_panel()
    {
        // NOTHING MAY THROW FROM A CALLBACK. root\WMI is reached through System.Management, which
        // can throw for reasons that have nothing to do with this app - a service restarting, a
        // repository being rebuilt - and this is reached from a window procedure.
        var panel = new FakePanelBrightness { ThrowOnRead = new InvalidOperationException("provider is unwell") };

        Assert.Null(Over(panel).Step(BrightnessLadder.Up));
        Assert.Empty(panel.Written);
    }

    [Fact]
    public void A_provider_that_throws_on_the_write_is_treated_as_a_refusal()
    {
        var panel = new FakePanelBrightness
        {
            State = new PanelBrightnessState(50, FakePanelBrightness.DenseLadder),
            ThrowOnSet = new UnauthorizedAccessException("no"),
        };

        Assert.Null(Over(panel).Step(BrightnessLadder.Up));
    }

    [Fact]
    public void A_direction_of_nothing_reads_nothing_and_writes_nothing()
    {
        var panel = new FakePanelBrightness();

        Assert.Null(Over(panel).Step(0));
        Assert.Equal(0, panel.Reads);
        Assert.Empty(panel.Written);
    }

    // ---- the null panel ----------------------------------------------------------------------

    [Fact]
    public void The_stand_in_for_a_machine_with_no_panel_answers_without_touching_anything()
    {
        // What AppServices holds until something real is built, and what every test rig that does
        // not care about brightness gets. It has to be safe to call, not merely present.
        var none = new NoPanelBrightness();

        Assert.Null(none.Read());
        Assert.False(none.Set(50));
        Assert.Null(new PanelBrightnessController(none).Step(BrightnessLadder.Up));
    }

    [Fact]
    public void A_null_panel_is_this_app_calling_itself_wrongly()
    {
        Assert.Throws<ArgumentNullException>(() => new PanelBrightnessController(null!));
    }

    [Fact]
    public void Nothing_a_press_can_do_escapes_as_an_exception()
    {
        // Every shape the panel can be in, stepped both ways.
        var states = new PanelBrightnessState?[]
        {
            null,
            new(50, Array.Empty<int>()),
            new(50, new[] { 50 }),
            new(50, FakePanelBrightness.DenseLadder),
            new(999, new[] { 0, 100 }),
            new(-5, new[] { 0, 100 }),
        };

        foreach (var state in states)
        foreach (var direction in new[] { BrightnessLadder.Down, 0, BrightnessLadder.Up })
        foreach (var breaks in new[] { false, true })
        {
            var panel = new FakePanelBrightness
            {
                State = state,
                ThrowOnRead = breaks ? new InvalidOperationException("x") : null,
            };

            var error = Record.Exception(() => new PanelBrightnessController(panel).Step(direction));
            Assert.Null(error);
        }
    }
}
