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
                     "OverlayForTouchpad", "OverlayForWifi",
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
    /// The four per-signal switches belong outside the gate as well, not only the master one.
    /// </summary>
    /// <remarks>The test above walks the master switch, which is the element most likely to be
    /// moved on purpose; a card reorganised so that only it escaped the gate would leave the
    /// three no-WMI features disabled on an unrecognised model with nothing failing.</remarks>
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
                 })
        foreach (var toggle in doc.Descendants().Where(e => BindsTo(e, property)))
            Assert.DoesNotContain(gated, g => g.Descendants().Contains(toggle));
    }

    [Fact]
    public void There_is_no_overlay_toggle_for_anything_windows_already_draws()
    {
        // Offering one would imply the app could draw for volume or display brightness. It
        // cannot: it never receives the volume keys, and it ignores the brightness report.
        var text = File.ReadAllText(SettingsPath);

        Assert.DoesNotContain("OverlayForVolume", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OverlayForBrightness", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The card says out loud that Windows keeps its own two overlays.
    /// </summary>
    /// <remarks>An owner who cannot find a volume switch has no way of telling a deliberate
    /// refusal from a missing feature, and would reasonably go looking for the setting that turns
    /// it on. Saying so is the difference between the two.</remarks>
    [Fact]
    public void The_card_explains_which_overlays_windows_keeps()
    {
        var text = File.ReadAllText(SettingsPath);

        Assert.Contains("volume", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("brightness", text, StringComparison.OrdinalIgnoreCase);
    }
}
