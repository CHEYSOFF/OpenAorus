using OpenAorus.App.Views;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The extended style word the overlay puts on its own HWND.
/// </summary>
/// <remarks>
/// <para>
/// What the three styles buy - a card that cannot take focus, cannot take a click and cannot
/// appear in Alt-Tab - needs a desktop to observe, and VERIFY 7.6 is where that is observed.
/// What they ARE does not need one, and that is the half a defect would live in: a card whose
/// <c>WS_EX_TRANSPARENT</c> was written <c>0x200</c> instead of <c>0x20</c> compiles, runs,
/// ships, and swallows every click at the position it draws at. Nothing on the machine reports
/// it and nothing here would have caught it before this file existed - deleting all three
/// constants from the call left the whole suite green.
/// </para>
/// <para>
/// So the composition is pinned by its value rather than by its spelling: asserting
/// <c>style | WsExTransparent</c> against itself would pass for any number the constant held.
/// The literals below are the documented ones from <c>winuser.h</c> and are written out here on
/// purpose, so that this file and the code being tested cannot be wrong together.
/// </para>
/// </remarks>
public class OverlayWindowStyleTests
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [Fact]
    public void The_card_asks_for_exactly_the_three_documented_styles()
    {
        // 0x080000A0. Written as the three literals so a wrong digit in any one of them is a
        // failure here rather than an overlay that misbehaves on hardware.
        Assert.Equal(
            WsExTransparent | WsExNoActivate | WsExToolWindow,
            OverlayWindow.WithOverlayStyles(0));
    }

    [Theory]
    // Not hit-tested at all, so a click lands on whatever is behind it - in any process.
    [InlineData(WsExTransparent)]
    // Never becomes the foreground window, so a keystroke aimed elsewhere is never lost.
    [InlineData(WsExNoActivate)]
    // Out of Alt-Tab.
    [InlineData(WsExToolWindow)]
    public void Every_one_of_the_three_is_set(int style)
    {
        Assert.Equal(style, OverlayWindow.WithOverlayStyles(0) & style);
    }

    [Fact]
    public void What_the_window_already_had_survives()
    {
        // WPF has put its own bits on the HWND by the time SourceInitialized runs - WS_EX_WINDOWEDGE
        // and whatever AllowsTransparency brought with it. Assigning rather than OR-ing would drop
        // them, which is a different overlay bug with the same silence.
        const int existing = 0x00000100 | 0x00080000;   // WS_EX_WINDOWEDGE | WS_EX_LAYERED

        var result = OverlayWindow.WithOverlayStyles(existing);

        Assert.Equal(existing, result & existing);
        Assert.Equal(WsExTransparent | WsExNoActivate | WsExToolWindow | existing, result);
    }

    [Fact]
    public void Adding_the_styles_to_a_window_that_has_them_changes_nothing()
    {
        // OR, not XOR or +: SourceInitialized runs once per HWND today, but a card whose styles
        // toggled off on a second pass would be indistinguishable from one that never had them.
        var once = OverlayWindow.WithOverlayStyles(0);

        Assert.Equal(once, OverlayWindow.WithOverlayStyles(once));
    }

    [Fact]
    public void Nothing_beyond_the_three_is_added()
    {
        // A fourth style would be a behaviour change nobody asked for - WS_EX_LAYERED here, say,
        // fights AllowsTransparency - and would arrive with no test naming it.
        Assert.Equal(0, OverlayWindow.WithOverlayStyles(0) & ~(WsExTransparent | WsExNoActivate | WsExToolWindow));
    }
}
