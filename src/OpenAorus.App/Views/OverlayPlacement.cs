namespace OpenAorus.App.Views;

/// <summary>
/// Where the overlay card sits, in device-independent units.
/// </summary>
/// <remarks>
/// Doubles rather than <c>Rect</c>: this project enables WPF and WinForms together, so
/// <c>Point</c> and <c>Size</c> are ambiguous and keeping them out of the signature costs
/// nothing. Choosing the monitor and converting its bounds to DIPs stays in the code-behind,
/// where a test cannot reach it - see VERIFY 7.8.
/// </remarks>
public static class OverlayPlacement
{
    /// <summary>How far above the bottom of the work area the card sits, in DIPs.</summary>
    /// <remarks>Roughly where Windows puts its own volume card, so the two do not stack when
    /// both are up during the verification steps.</remarks>
    public const double BottomMarginDip = 96;

    /// <summary>The card's left edge, centred on the work area.</summary>
    /// <remarks>The clamp is inside the origin, not outside it: a card wider than its screen
    /// belongs at that screen's left edge, and on a monitor left of the primary that edge is a
    /// negative number rather than zero.</remarks>
    /// <param name="areaLeft">The work area's left edge.</param>
    /// <param name="areaWidth">The work area's width.</param>
    /// <param name="widthDip">The card's width.</param>
    /// <returns>The card's left edge.</returns>
    public static double Left(double areaLeft, double areaWidth, double widthDip) =>
        areaLeft + Math.Max(0, (areaWidth - widthDip) / 2);

    /// <summary>The card's top edge, sitting <see cref="BottomMarginDip"/> above the bottom.</summary>
    /// <remarks>Clamped to the top of the work area: a card taller than the screen belongs on
    /// screen rather than above it. Clamped the same way as <see cref="Left"/> and for the same
    /// reason - to the work area's own origin, not to the desktop's.</remarks>
    /// <param name="areaTop">The work area's top edge.</param>
    /// <param name="areaHeight">The work area's height.</param>
    /// <param name="heightDip">The card's height.</param>
    /// <returns>The card's top edge.</returns>
    public static double Top(double areaTop, double areaHeight, double heightDip) =>
        areaTop + Math.Max(0, areaHeight - heightDip - BottomMarginDip);
}
