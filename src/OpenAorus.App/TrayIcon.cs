using System.Drawing;
using System.Windows.Forms;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Ui;

namespace OpenAorus.App;

public sealed class TrayIcon : IDisposable
{
    /// <summary>The app's own icon, and the one <c>ApplicationIcon</c> puts on the executable and
    /// the window. A bold A on the accent orange.</summary>
    private const string OkResource = "OpenAorus.App.openaorus.ico";

    /// <summary>The error state: the same tile in the theme's error red, carrying an exclamation
    /// instead of the A.</summary>
    /// <remarks>
    /// The glyph changes as well as the colour, and deliberately. At the 16 px the notification
    /// area actually draws, orange and red are two warm blocks of near-identical value - findable
    /// side by side, but not "at a glance" on a taskbar the owner is not studying, and not at all
    /// to the commonest colour vision deficiencies, which flatten exactly that pair. A different
    /// shape survives both. The tile, its size and its corner radius stay put, so the icon is
    /// still recognisably this app's.
    /// </remarks>
    private const string ErrorResource = "OpenAorus.App.openaorus-error.ico";

    private readonly NotifyIcon _icon = new();
    private readonly MainViewModel _vm;
    private readonly Icon _ok;
    private readonly Icon _err;
    private bool _disposed;

    public TrayIcon(MainViewModel vm, Action toggleWindow, Action quit)
    {
        _vm = vm;
        _ok = Load(OkResource);
        _err = Load(ErrorResource);
        _icon.Icon = _ok;
        _icon.Text = "OpenAorus";
        _icon.Visible = true;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) toggleWindow(); };

        var menu = new ContextMenuStrip();
        foreach (var mode in Enum.GetValues<FanMode>())
        {
            var m = mode;
            menu.Items.Add(mode.ToString(), null, async (_, _) => await _vm.SelectModeCommand.ExecuteAsync(m));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) => toggleWindow());
        menu.Items.Add("Quit", null, (_, _) => quit());
        _icon.ContextMenuStrip = menu;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Sensors) or nameof(MainViewModel.SelectedMode) or nameof(MainViewModel.Banner))
                Refresh();
        };
    }

    public void Refresh()
    {
        var text = _vm.TrayTooltip;
        _icon.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
        _icon.Icon = _vm.Banner == BannerKind.Error ? _err : _ok;
    }

    /// <summary>
    /// Pulls one authored icon out of the assembly at the size the shell is about to draw it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Authored rather than drawn. The 16 px entry is hand-fitted to its own pixel grid - it is
    /// not the 256 scaled down - because 16 px is the size the notification area actually shows
    /// and the size an icon fails at.
    /// </para>
    /// <para>
    /// <see cref="SystemInformation.SmallIconSize"/> is the size Windows wants for the
    /// notification area at the current DPI: 16 at 100 %, 20 at 125 %, 24 at 150 %. The file
    /// carries an entry authored at each of those, so this picks a real one rather than making
    /// the shell resample. A size with no exact entry falls back to the nearest, which is what
    /// the 32 and 48 are there for.
    /// </para>
    /// <para>
    /// This is also why nothing here calls <c>DestroyIcon</c> any more. The old code drew a
    /// bitmap and wrapped <see cref="Bitmap.GetHicon"/> in <see cref="Icon.FromHandle"/>, which
    /// does not take ownership, so the raw handle had to be kept and destroyed by hand. An
    /// <see cref="Icon"/> built from a stream owns its handle and gives it back in
    /// <see cref="Icon.Dispose"/>; destroying it again here would be a double free, not a leak fix.
    /// </para>
    /// </remarks>
    /// <param name="name">The manifest resource name of the .ico.</param>
    /// <returns>The icon, owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">The resource is not in the assembly.</exception>
    private static Icon Load(string name)
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Icon resource '{name}' is missing from the assembly.");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _ok.Dispose();
        _err.Dispose();
    }
}
