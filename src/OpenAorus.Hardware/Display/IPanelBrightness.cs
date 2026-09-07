namespace OpenAorus.Hardware.Display;

/// <summary>What the panel says about itself: where it is, and where it will let itself be put.</summary>
/// <param name="Current">The level in force, as the panel reports it. Not necessarily one of
/// <paramref name="SupportedLevels"/> - something else on the machine can have set it, and firmware
/// rounds.</param>
/// <param name="SupportedLevels">Every level the panel will accept. Documented ascending; nothing
/// downstream relies on that, because "documented" is not "checked".</param>
public sealed record PanelBrightnessState(int Current, IReadOnlyList<int> SupportedLevels);

/// <summary>
/// The only door to the display panel's own brightness. Real implementation:
/// <see cref="WmiPanelBrightness"/>.
/// </summary>
/// <remarks>
/// <para>
/// THE REASON THIS EXISTS AT ALL. On an AORUS 17G KD the firmware reports the brightness keys and
/// then does not act on them: the panel does not change. Gigabyte's own software was performing the
/// change in software, and <c>GetBrightness</c> on the Gigabyte WMI interface answers
/// <c>Invalid object</c> on this model, so the embedded controller does not expose it either. If
/// this app wants those two keys to work, it has to set the brightness itself. See the "Observed on
/// hardware" section of <c>docs/research/fn-hotkey-signals.md</c>.
/// </para>
/// <para>
/// A seam for the same reason <see cref="Wmi.IGigabyteWmi"/> and <see cref="Lighting.IKeyboardHid"/>
/// are ones: the policy and the view model have to be testable without a screen someone watches by
/// eye, and "what happens on a machine with no controllable panel" has to be answerable at all.
/// </para>
/// <para>
/// IMPLEMENTATIONS SHOULD NOT THROW. This is reached from a raw-input window procedure.
/// <see cref="WmiPanelBrightness"/> catches everything and answers null or false, and
/// <see cref="PanelBrightnessController"/> catches again on the far side so that an implementation
/// which breaks the contract still cannot take a callback down with it.
/// </para>
/// </remarks>
public interface IPanelBrightness
{
    /// <summary>Reads the panel, or null when there is no controllable one.</summary>
    /// <remarks>Null is the ordinary answer on a desktop, on a machine driving only external
    /// monitors, and on a laptop whose panel exposes no WMI brightness instance. It is not a
    /// failure to report.</remarks>
    PanelBrightnessState? Read();

    /// <summary>Puts the panel on one level.</summary>
    /// <param name="level">A level from <see cref="PanelBrightnessState.SupportedLevels"/>.</param>
    /// <returns>True if the panel took it; false if it refused or there was no panel.</returns>
    bool Set(int level);
}

/// <summary>The panel on a machine that has none. Answers, and touches nothing.</summary>
/// <remarks>What <see cref="OpenAorus.App"/> holds before anything real is built, and what a test
/// rig with no interest in brightness gets. A null <see cref="IPanelBrightness"/> would push a null
/// check onto every call site instead, on the path where a missed one is an exception inside a
/// window procedure.</remarks>
public sealed class NoPanelBrightness : IPanelBrightness
{
    /// <inheritdoc />
    public PanelBrightnessState? Read() => null;

    /// <inheritdoc />
    public bool Set(int level) => false;
}
