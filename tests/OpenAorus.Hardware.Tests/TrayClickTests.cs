using OpenAorus.App.Views;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// What a click on the notification-area icon should do.
/// </summary>
/// <remarks>
/// The window itself needs a desktop and an STA thread, so only the decision is testable. That
/// is where the defect was: the old rule hid whenever the window was visible, so a window the
/// owner had clicked away from was hidden by the click meant to raise it, and the second click
/// brought it back. Hence "sometimes twice, sometimes once".
/// </remarks>
public class TrayClickTests
{
    [Fact]
    public void The_window_you_are_looking_at_is_hidden()
        => Assert.True(MainWindow.ShouldHideOnTrayClick(isVisible: true, isMinimized: false, isForeground: true));

    /// <summary>The regression. Visible but behind something else must be raised, not hidden.</summary>
    [Fact]
    public void A_window_that_is_open_but_behind_something_is_raised_rather_than_hidden()
        => Assert.False(MainWindow.ShouldHideOnTrayClick(isVisible: true, isMinimized: false, isForeground: false));

    [Fact]
    public void A_hidden_window_is_raised()
        => Assert.False(MainWindow.ShouldHideOnTrayClick(isVisible: false, isMinimized: false, isForeground: false));

    /// <summary>Minimised counts as not on screen, whatever Windows says about focus.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_minimised_window_is_raised(bool isForeground)
        => Assert.False(MainWindow.ShouldHideOnTrayClick(isVisible: true, isMinimized: true, isForeground));
}
