using System.IO;
using System.Xml.Linq;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The things about the schema card that the XAML compiler cannot check.
/// </summary>
/// <remarks>
/// Read as plain XML for the same reason as <see cref="XamlResourceTests"/> and
/// <see cref="HotkeySettingsUiTests"/>: loading it properly needs an Application on an STA thread,
/// which is process-global and would make every other test in this assembly order-dependent.
/// </remarks>
public class SchemaSettingsUiTests
{
    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "xaml", "Views", "SettingsWindow.xaml");

    private static XDocument Settings() => XDocument.Load(SettingsPath);

    private static bool BindsTo(XElement e, string member) =>
        e.Attributes().Any(a => a.Value.Contains($"Binding {member}", StringComparison.Ordinal));

    private static List<XElement> Gated(XDocument doc) =>
        doc.Descendants()
            .Where(e => e.Attribute("IsEnabled")?.Value.Contains("CanWrite", StringComparison.Ordinal) == true)
            .ToList();

    [Fact]
    public void The_three_buttons_are_in_the_settings_window()
    {
        var doc = Settings();

        foreach (var command in new[] { "InstallCommand", "RemoveCommand", "RunGatesCommand" })
            Assert.True(doc.Descendants().Any(e => BindsTo(e, command)), $"nothing binds {command}");
    }

    [Fact]
    public void The_card_is_outside_the_gate_that_disables_the_rest_of_the_dialog()
    {
        // This is the card that fixes the condition disabling everything else. Greying it out
        // would leave the owner in a modal dialog whose only working control is the one that
        // cannot help them.
        var doc = Settings();
        var gated = Gated(doc);
        Assert.NotEmpty(gated);

        foreach (var command in new[] { "InstallCommand", "RemoveCommand", "RunGatesCommand" })
        {
            var buttons = doc.Descendants().Where(e => BindsTo(e, command)).ToList();
            Assert.NotEmpty(buttons);
            foreach (var button in buttons)
                Assert.DoesNotContain(gated, g => g.Descendants().Contains(button));
        }
    }

    [Fact]
    public void The_state_and_the_result_are_outside_it_too()
    {
        // Text the owner reads about a card that works, greyed out because a different card does
        // not, would be the gate reaching this one by the back door.
        var doc = Settings();
        var gated = Gated(doc);

        foreach (var member in new[] { "StatusText", "Message" })
        foreach (var block in doc.Descendants().Where(e => BindsTo(e, member)))
            Assert.DoesNotContain(gated, g => g.Descendants().Contains(block));
    }

    [Fact]
    public void No_button_is_offered_for_an_action_the_state_machine_forbids()
    {
        // Each button's visibility follows the permission the state machine grants, so the card
        // never offers Install on a Control Center machine or Remove on a machine with nothing on
        // it. A DataTrigger, because these are real bools - see Converters.cs for why an
        // object-typed one would silently never fire.
        var text = File.ReadAllText(SettingsPath);

        foreach (var permission in new[] { "CanInstall", "CanRemove", "CanRunGates" })
            Assert.Contains($"Binding {permission}", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The card says what it replaces, that it needs administrator rights, and that it undoes.
    /// </summary>
    /// <remarks>The owner in section 1 of the design had no way of knowing that uninstalling
    /// Control Center took a file with it. A card that offered a button without saying that would
    /// be asking them to authorise a system change on trust.</remarks>
    [Fact]
    public void The_card_explains_itself_before_it_offers_anything()
    {
        // Read out of the card itself rather than out of the file, which already says "Control
        // Center" three cards higher up for an unrelated reason.
        var prose = string.Join(" ", SchemaCard().Descendants()
            .Select(e => e.Attribute("Text")?.Value)
            .Where(t => t is not null));

        Assert.Contains("Control Center", prose, StringComparison.Ordinal);
        Assert.Contains("administrator", prose, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("back", prose, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The one element the whole card hangs off.</summary>
    private static XElement SchemaCard() =>
        Settings().Descendants().Single(e =>
            e.Attribute("DataContext")?.Value.Contains("Binding Schema", StringComparison.Ordinal) == true);
}
