using UserControl = System.Windows.Controls.UserControl;

namespace OpenAorus.App.Views;

/// <summary>
/// The Lighting half of the window: the effect and its parameters, the presets, and the per-key
/// editor for the one effect that has colours of its own.
/// </summary>
public partial class LightingPanel : UserControl
{
    public LightingPanel() => InitializeComponent();
}
