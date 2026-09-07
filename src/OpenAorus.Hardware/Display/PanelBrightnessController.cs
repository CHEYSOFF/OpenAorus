namespace OpenAorus.Hardware.Display;

/// <summary>
/// One press of a brightness key: read the panel, choose the next level, write it back.
/// </summary>
/// <remarks>
/// <para>
/// The acting half of the brightness keys, with <see cref="BrightnessLadder"/> as the deciding
/// half - the same division of labour as <see cref="Fans.FanSafety"/> and the fan watchdog. The
/// firmware on this chassis reports the keys and does not act on them, so without this the keys do
/// nothing at all; see <see cref="IPanelBrightness"/> for how that was established.
/// </para>
/// <para>
/// READ EVERY TIME, never cached. The panel is not this app's to own: Windows' own slider, the
/// power policy going onto battery, and any other tool on the machine all move it, and stepping
/// from a remembered level would jump the screen back to wherever this app last left it.
/// </para>
/// <para>
/// NOTHING ESCAPES. This is reached from a raw-input window procedure, by way of the dispatcher,
/// and <c>root\WMI</c> through <c>System.Management</c> can throw for reasons that have nothing to
/// do with this app. Every failure - no panel, no ladder, a refused write, a provider that threw -
/// comes back as null, which the caller reads as "the screen did not move, so say nothing".
/// </para>
/// <para>
/// Locked, so that the read and the write either side of one decision cannot be interleaved with
/// another press's. The debouncer holds repeats to one per 250 ms and the app posts them to one
/// thread, so contention is not expected; the lock is what makes that a fact about this class
/// rather than about its callers.
/// </para>
/// </remarks>
public sealed class PanelBrightnessController
{
    private readonly IPanelBrightness _panel;
    private readonly object _gate = new();

    /// <param name="panel">The panel to drive. <see cref="NoPanelBrightness"/> for a machine with
    /// none - never null, so no call site has to remember a null check on a callback path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="panel"/> is null.</exception>
    public PanelBrightnessController(IPanelBrightness panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        _panel = panel;
    }

    /// <summary>Moves the panel one step.</summary>
    /// <param name="direction">
    /// <see cref="BrightnessLadder.Up"/> or <see cref="BrightnessLadder.Down"/>.
    /// </param>
    /// <returns>
    /// The level in force afterwards, or null if nothing moved and nothing can be said about it.
    /// A press at the end of the ladder's travel returns the level it is already on rather than
    /// null: the key is not broken, the panel is at its limit, and an overlay redrawing "100 %" is
    /// the honest thing to show.
    /// </returns>
    public int? Step(int direction)
    {
        if (direction == 0) return null;

        lock (_gate)
        {
            try
            {
                var state = _panel.Read();
                if (state is null) return null;                       // no controllable panel

                if (BrightnessLadder.Next(state.SupportedLevels, state.Current, direction)
                    is not { } target) return null;                   // a panel with no ladder

                // Already there. Writing the level the panel is on would be a WMI round trip per
                // repeat of a key held at the end of its travel, for no change on screen.
                if (target == state.Current) return target;

                return _panel.Set(target) ? target : null;
            }
            catch (Exception)
            {
                // Broad on purpose, and swallowed rather than reported. An implementation is not
                // meant to throw at all; if one does, the alternative here is an exception on a
                // window procedure, and a brightness key that quietly does nothing is a far
                // smaller failure than an app that falls over when one is pressed.
                return null;
            }
        }
    }
}
