using System.Windows.Automation.Peers;
using Border = System.Windows.Controls.Border;

namespace OpenAorus.App.Views;

/// <summary>
/// The shape one key is drawn as in the per-key editor.
/// </summary>
/// <remarks>
/// A <see cref="Border"/> and nothing more, except that it answers with an automation peer. A
/// plain Border creates none, and an element with no peer is not in the automation tree at all -
/// so <c>AutomationProperties.Name</c> on one is silently inert and a screen reader falls back to
/// reading the bound item's <c>ToString()</c>, which is a type name. The keys are the only way to
/// set a colour, so they have to be announced by the name and slot the tooltip shows.
/// </remarks>
public sealed class KeyFace : Border
{
    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new KeyFacePeer(this);

    private sealed class KeyFacePeer : FrameworkElementAutomationPeer
    {
        public KeyFacePeer(KeyFace owner) : base(owner) { }

        // Announced as a button because that is how it behaves: it takes focus, it is reached by
        // tab, and space or enter paints it.
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(KeyFace);
    }
}
