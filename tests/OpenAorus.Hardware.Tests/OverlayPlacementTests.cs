using OpenAorus.App.Views;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Where the card sits, in device-independent units, given a work area and a card size.
/// </summary>
/// <remarks>
/// Pure doubles rather than <c>Rect</c> or <c>Size</c>: this project enables WPF and WinForms
/// together, so <c>Point</c> and <c>Size</c> are ambiguous types and keeping them out of the
/// signature is cheaper than aliasing them at every call site. Which monitor's work area gets
/// passed in, and the DPI it is measured in, are the code-behind's problem and VERIFY's.
/// </remarks>
public class OverlayPlacementTests
{
    [Fact]
    public void The_card_is_centred_horizontally_on_its_screen()
    {
        Assert.Equal(860, OverlayPlacement.Left(areaLeft: 0, areaWidth: 1920, widthDip: 200));
    }

    [Fact]
    public void A_second_monitor_to_the_left_is_centred_on_too()
    {
        // Negative origins are ordinary on a multi-monitor desktop.
        Assert.Equal(-1060, OverlayPlacement.Left(areaLeft: -1920, areaWidth: 1920, widthDip: 200));
    }

    [Fact]
    public void The_card_sits_above_the_bottom_of_the_work_area_by_the_margin()
    {
        var top = OverlayPlacement.Top(areaTop: 0, areaHeight: 1080, heightDip: 80);

        Assert.Equal(1080 - 80 - OverlayPlacement.BottomMarginDip, top);
    }

    [Fact]
    public void The_work_areas_own_origin_is_respected()
    {
        var top = OverlayPlacement.Top(areaTop: 100, areaHeight: 900, heightDip: 80);

        Assert.Equal(100 + 900 - 80 - OverlayPlacement.BottomMarginDip, top);
    }

    [Fact]
    public void A_card_taller_than_its_screen_is_pinned_to_the_top_rather_than_pushed_off_it()
    {
        var top = OverlayPlacement.Top(areaTop: 0, areaHeight: 100, heightDip: 400);

        Assert.Equal(0, top);
    }

    [Fact]
    public void A_card_wider_than_its_screen_is_pinned_to_the_left_edge()
    {
        Assert.Equal(0, OverlayPlacement.Left(areaLeft: 0, areaWidth: 200, widthDip: 400));
    }

    /// <summary>
    /// The clamp has to respect the screen it is clamping onto, not the desktop origin.
    /// </summary>
    /// <remarks>
    /// The two clamp tests above both use a work area at 0,0, where "pinned to the edge" and
    /// "pinned to zero" are the same number - so a <c>Math.Max(0, ...)</c> written outside the
    /// origin instead of inside it passes both. On a second monitor left of or above the primary
    /// that mistake puts the card on the wrong screen entirely.
    /// </remarks>
    [Fact]
    public void An_oversized_card_is_pinned_to_its_own_screens_edge_not_to_the_desktop_origin()
    {
        Assert.Equal(-1920, OverlayPlacement.Left(areaLeft: -1920, areaWidth: 200, widthDip: 400));
        Assert.Equal(-600, OverlayPlacement.Top(areaTop: -600, areaHeight: 100, heightDip: 400));
    }
}
