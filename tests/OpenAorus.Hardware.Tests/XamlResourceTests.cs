using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Reads the markup as plain XML and checks the two things the XAML compiler does not.
/// </summary>
/// <remarks>
/// <para>
/// A <c>StaticResource</c> key is resolved when the element is realised, not when the BAML is
/// built, so a mistyped key compiles cleanly and throws the first time the panel is shown - and
/// the per-key editor is shown only under the Custom effect, which is exactly where a typo would
/// sit unnoticed the longest.
/// </para>
/// <para>
/// Loading the XAML properly would need an <c>Application</c> on an STA thread, which is process
/// global and would make every other test in this assembly order-dependent. The keys are plain
/// text in a plain XML file, so they are read as such: no WPF is loaded and nothing here takes
/// longer than a few milliseconds.
/// </para>
/// </remarks>
public class XamlResourceTests
{
    private static readonly Regex ResourceRef = new(@"\{StaticResource\s+([^}]+)\}", RegexOptions.Compiled);

    private static string XamlDirectory => Path.Combine(AppContext.BaseDirectory, "xaml");

    private static IReadOnlyList<string> ViewFiles =>
        Directory.GetFiles(Path.Combine(XamlDirectory, "Views"), "*.xaml");

    private static string ThemeFile => Path.Combine(XamlDirectory, "Themes", "Dark.xaml");

    /// <summary>Every <c>x:Key</c> declared anywhere in one file.</summary>
    private static HashSet<string> KeysDefinedIn(string path)
    {
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        return XDocument.Load(path)
            .Descendants()
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Every key a file asks for, skipping the markup-extension form - <c>{StaticResource
    /// {x:Type TextBlock}}</c> names a type rather than a key and cannot be mistyped silently.
    /// </summary>
    private static IEnumerable<string> KeysReferencedIn(string path) =>
        ResourceRef.Matches(File.ReadAllText(path))
            .Select(m => m.Groups[1].Value.Trim())
            .Where(k => !k.StartsWith('{'));

    /// <summary>
    /// The copy items in the .csproj are the only thing putting the markup next to the tests, so
    /// a rename that quietly stops copying them would otherwise turn every check below into a
    /// loop over nothing that passes.
    /// </summary>
    [Fact]
    public void The_markup_is_actually_next_to_the_tests()
    {
        Assert.True(Directory.Exists(XamlDirectory), $"no xaml directory at {XamlDirectory}");
        Assert.True(File.Exists(ThemeFile), $"no theme at {ThemeFile}");
        Assert.NotEmpty(ViewFiles);
        Assert.Contains(ViewFiles, f => Path.GetFileName(f) == "KeyboardCanvas.xaml");
        Assert.NotEmpty(ViewFiles.SelectMany(KeysReferencedIn));
    }

    [Fact]
    public void Every_static_resource_a_view_asks_for_is_defined_locally_or_in_the_theme()
    {
        var theme = KeysDefinedIn(ThemeFile);

        var missing = new List<string>();
        foreach (var file in ViewFiles)
        {
            var local = KeysDefinedIn(file);
            foreach (var key in KeysReferencedIn(file))
                if (!local.Contains(key) && !theme.Contains(key))
                    missing.Add($"{Path.GetFileName(file)}: {key}");
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void The_theme_resolves_its_own_keys()
    {
        var theme = KeysDefinedIn(ThemeFile);

        var missing = KeysReferencedIn(ThemeFile).Where(k => !theme.Contains(k)).ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// The converters reach for their brushes with <c>FindResource</c> at run time rather than
    /// through markup, so the check above cannot see them. That call throws rather than
    /// returning null on a bad key, and it runs while a banner is being painted - the moment
    /// something has already gone wrong and the owner is being told about it.
    /// </summary>
    [Fact]
    public void Every_resource_the_converters_look_up_at_run_time_is_defined_in_the_theme()
    {
        var source = Path.Combine(XamlDirectory, "Converters.cs.txt");
        Assert.True(File.Exists(source), $"no converter source at {source}");

        var keys = Regex.Matches(File.ReadAllText(source), @"FindResource\(""([^""]+)""\)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A rename that stopped the converters using FindResource would otherwise leave this
        // looping over nothing, which is the failure this whole file exists to avoid.
        Assert.NotEmpty(keys);

        var theme = KeysDefinedIn(ThemeFile);
        Assert.Empty(keys.Where(k => !theme.Contains(k)));
    }

    /// <summary>
    /// The hex boxes have to push every keystroke into the view model. WPF's default for
    /// <c>TextBox.Text</c> is <c>LostFocus</c>, and the thing the owner clicks next is a key in
    /// the picture, which is not a focus scope - so a typed colour would be dropped on the floor
    /// and the key would paint in the previous colour while the box still read the new one.
    /// </summary>
    [Theory]
    [InlineData("KeyboardCanvas.xaml", "BrushHex")]
    [InlineData("LightingPanel.xaml", "ColorHex")]
    [InlineData("LightingPanel.xaml", "SecondColorHex")]
    public void The_hex_boxes_push_every_keystroke(string fileName, string property)
    {
        var text = File.ReadAllText(Path.Combine(XamlDirectory, "Views", fileName));

        var binding = Regex.Match(text, @"\{Binding " + Regex.Escape(property) + @"[^}]*\}");

        Assert.True(binding.Success, $"{fileName} does not bind {property}");
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", binding.Value, StringComparison.Ordinal);
    }
}
