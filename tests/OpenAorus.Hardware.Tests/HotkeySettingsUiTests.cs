using System.IO;
using System.Xml.Linq;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The two things about the hotkey card that the XAML compiler cannot check.
/// </summary>
/// <remarks>
/// Read as plain XML for the same reason as <see cref="XamlResourceTests"/>: loading it properly
/// needs an Application on an STA thread, which is process-global and would make every other test
/// in this assembly order-dependent.
/// </remarks>
public class HotkeySettingsUiTests
{
    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "xaml", "Views", "SettingsWindow.xaml");

    private static XDocument Settings() => XDocument.Load(SettingsPath);

    private static bool BindsTo(XElement e, string property) =>
        e.Attributes().Any(a => a.Value.Contains($"Binding {property}", StringComparison.Ordinal));

    [Fact]
    public void The_hotkey_toggles_are_in_the_settings_window()
    {
        var doc = Settings();

        foreach (var property in new[]
                 {
                     "HotkeysEnabled", "OverlayForFanMode", "OverlayForBacklight",
                     "OverlayForTouchpad", "OverlayForWifi", "OverlayForPanelBrightness",
                 })
            Assert.True(doc.Descendants().Any(e => BindsTo(e, property)), $"nothing binds {property}");
    }

    [Fact]
    public void The_hotkey_card_is_outside_the_gate_that_disables_the_dialog_on_an_unknown_model()
    {
        // Touchpad, Wi-Fi and backlight need no WMI at all, so an unrecognised model must not
        // lose them. Only fan cycling is affected there, and the fan controller already refuses
        // that with a message of its own.
        var doc = Settings();

        var gated = doc.Descendants()
            .Where(e => e.Attribute("IsEnabled")?.Value.Contains("CanWrite", StringComparison.Ordinal) == true)
            .ToList();
        Assert.NotEmpty(gated);

        var toggles = doc.Descendants().Where(e => BindsTo(e, "HotkeysEnabled")).ToList();
        Assert.NotEmpty(toggles);

        foreach (var toggle in toggles)
            Assert.DoesNotContain(gated, g => g.Descendants().Contains(toggle));
    }

    /// <summary>
    /// The per-signal switches belong outside the gate as well, not only the master one.
    /// </summary>
    /// <remarks>The test above walks the master switch, which is the element most likely to be
    /// moved on purpose; a card reorganised so that only it escaped the gate would leave the
    /// no-WMI features disabled on an unrecognised model with nothing failing. Panel brightness
    /// belongs in that list for a reason of its own: it goes through Windows' monitor classes and
    /// not through Gigabyte's interface at all, so an unrecognised model does not affect it.</remarks>
    [Fact]
    public void No_overlay_switch_is_inside_that_gate_either()
    {
        var doc = Settings();

        var gated = doc.Descendants()
            .Where(e => e.Attribute("IsEnabled")?.Value.Contains("CanWrite", StringComparison.Ordinal) == true)
            .ToList();

        foreach (var property in new[]
                 {
                     "OverlayForFanMode", "OverlayForBacklight", "OverlayForTouchpad", "OverlayForWifi",
                     "OverlayForPanelBrightness",
                 })
        foreach (var toggle in doc.Descendants().Where(e => BindsTo(e, property)))
            Assert.DoesNotContain(gated, g => g.Descendants().Contains(toggle));
    }

    [Fact]
    public void There_is_no_overlay_toggle_for_volume()
    {
        // Offering one would imply the app could draw for volume. It cannot, and not as a matter
        // of policy: the volume keys live on the consumer-control collection this app never
        // registers, so they never arrive and there is no signal to switch on.
        //
        // The panel-brightness switch that does exist is not this. It governs the two Fn keys the
        // firmware reports and does not act on, which Windows draws nothing for because the change
        // this app makes never reaches its hotkey path; the 9-byte report that says the brightness
        // has already changed is still refused, and still has no switch.
        var text = File.ReadAllText(SettingsPath);

        Assert.DoesNotContain("OverlayForVolume", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OverlayForDisplayBrightness", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The card says out loud that Windows keeps the volume overlay, and that this app now keeps
    /// the brightness one.
    /// </summary>
    /// <remarks>An owner who cannot find a volume switch has no way of telling a deliberate
    /// refusal from a missing feature, and would reasonably go looking for the setting that turns
    /// it on. Saying so is the difference between the two. Brightness needs saying for the
    /// opposite reason: it is the one card that starts on, and an overlay nobody asked for is
    /// exactly the complaint this release exists to answer, so the card has to explain itself.</remarks>
    [Fact]
    public void The_card_explains_who_draws_what()
    {
        var text = File.ReadAllText(SettingsPath);

        Assert.Contains("volume", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("brightness", text, StringComparison.OrdinalIgnoreCase);
    }
}
