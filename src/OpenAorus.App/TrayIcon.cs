using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Ui;

namespace OpenAorus.App;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly MainViewModel _vm;
    private readonly Icon _ok;
    private readonly Icon _err;
    private readonly IntPtr _okHandle;
    private readonly IntPtr _errHandle;
    private bool _disposed;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public TrayIcon(MainViewModel vm, Action toggleWindow, Action quit)
    {
        _vm = vm;
        _ok = Draw(Color.FromArgb(0xFF, 0x7A, 0x1A), out _okHandle);
        _err = Draw(Color.FromArgb(0xD6, 0x45, 0x45), out _errHandle);
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

    private static Icon Draw(Color color, out IntPtr handle)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.FromArgb(0x15, 0x17, 0x1B), 3);
            g.DrawLine(pen, 16, 8, 16, 24);   // simple "fan blade" glyph
            g.DrawLine(pen, 8, 16, 24, 16);
        }
        handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _ok.Dispose();
        _err.Dispose();
        // Icon.FromHandle does not take ownership of the HICON, so the GDI handle must be destroyed
        // separately after the Icon wrapper is disposed.
        DestroyIcon(_okHandle);
        DestroyIcon(_errHandle);
    }
}
