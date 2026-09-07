using System.IO;
using System.Xml.Linq;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The declarations that keep the overlay from behaving like a window.
/// </summary>
/// <remarks>
/// <para>
/// None of this can be observed without a desktop: whether the card really refuses focus, really
/// lets a click through and really stays out of Alt-Tab is hardware verification, and it is in
/// <c>VERIFY.md</c> for that reason. What can be checked here is that the declarations which buy
/// those properties are still present - deleting <c>ShowActivated="False"</c> costs nothing at
/// compile time and nothing at run time until someone is typing when a hotkey fires.
/// </para>
/// <para>
/// Read as plain XML, for the reason given in <see cref="XamlResourceTests"/>.
/// </para>
/// </remarks>
public class OverlayWindowMarkupTests
{
    private static string OverlayPath =>
        Path.Combine(AppContext.BaseDirectory, "xaml", "Views", "OverlayWindow.xaml");

    private static XElement Root() => XDocument.Load(OverlayPath).Root!;

    [Theory]
    // Never becomes the foreground window, so a keystroke in another program is never lost.
    [InlineData("ShowActivated", "False")]
    // Out of the taskbar; WS_EX_TOOLWINDOW in the code-behind takes it out of Alt-Tab too.
    [InlineData("ShowInTaskbar", "False")]
    // Above the window the owner is working in - the point of an on-screen display.
    [InlineData("Topmost", "True")]
    // A click where the card is drawn reaches what is behind it. WS_EX_TRANSPARENT is the half
    // of this that works against other processes; this half stops the card taking the hit first.
    [InlineData("IsHitTestVisible", "False")]
    [InlineData("Focusable", "False")]
    // Borderless and transparent: no caption to drag, no frame to see.
    [InlineData("WindowStyle", "None")]
    [InlineData("AllowsTransparency", "True")]
    [InlineData("ResizeMode", "NoResize")]
    public void The_overlay_declares_the_property_that_keeps_it_out_of_the_way(string name, string value)
    {
        Assert.Equal(value, Root().Attribute(name)?.Value);
    }

    /// <summary>
    /// The card is placed by hand, on the screen the pointer is on.
    /// </summary>
    /// <remarks><c>CenterScreen</c> would put it in the middle of the primary monitor whatever
    /// <see cref="OpenAorus.App.Views.OverlayPlacement"/> worked out.</remarks>
    [Fact]
    public void Nothing_else_gets_to_place_the_card()
    {
        var startup = Root().Attribute("WindowStartupLocation")?.Value;

        Assert.True(startup is null or "Manual", $"WindowStartupLocation is {startup}");
    }

    /// <summary>
    /// The overlay must not be able to draw for volume or display brightness.
    /// </summary>
    /// <remarks>Structural, not cosmetic: the card is a dumb text holder with no knowledge of
    /// signals at all, so there is nothing in it that could special-case one. A binding appearing
    /// here would be the first sign of that changing.</remarks>
    [Fact]
    public void The_card_knows_nothing_about_which_signal_it_is_drawing_for()
    {
        var text = File.ReadAllText(OverlayPath);

        Assert.DoesNotContain("Binding", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Volume", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Brightness", text, StringComparison.OrdinalIgnoreCase);
    }
}
