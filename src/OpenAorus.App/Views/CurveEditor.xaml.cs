using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenAorus.App.ViewModels;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using SizeChangedEventArgs = System.Windows.SizeChangedEventArgs;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace OpenAorus.App.Views;

public partial class CurveEditor : UserControl
{
    private CurveEditorViewModel? _vm;
    private CurvePointVm? _dragging;

    public CurveEditor()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.Changed -= Redraw;
            _vm = e.NewValue as CurveEditorViewModel;
            if (_vm is not null) _vm.Changed += Redraw;
            Redraw();
        };
    }

    public Button Apply => ApplyButton;

    private double W => Math.Max(1, Plot.ActualWidth);
    private double H => Math.Max(1, Plot.ActualHeight);
    private Point ToCanvas(int temp, int duty) => new(temp / 100.0 * W, H - duty / 100.0 * H);
    private (int temp, int duty) FromCanvas(Point p) =>
        ((int)Math.Round(Math.Clamp(p.X / W, 0, 1) * 100), (int)Math.Round(Math.Clamp(1 - p.Y / H, 0, 1) * 100));

    private void Redraw()
    {
        Plot.Children.Clear();
        if (_vm is null) return;
        var grid = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        for (var i = 1; i < 10; i++)
        {
            Plot.Children.Add(new Line { X1 = i * W / 10, X2 = i * W / 10, Y1 = 0, Y2 = H, Stroke = grid, StrokeThickness = 1 });
            Plot.Children.Add(new Line { X1 = 0, X2 = W, Y1 = i * H / 10, Y2 = i * H / 10, Stroke = grid, StrokeThickness = 1 });
        }
        var accent = (Brush)FindResource("Accent");
        var poly = new Polyline { Stroke = accent, StrokeThickness = 2 };
        foreach (var p in _vm.Points) poly.Points.Add(ToCanvas(p.Temperature, p.DutyPercent));
        Plot.Children.Add(poly);
        foreach (var p in _vm.Points)
        {
            var c = ToCanvas(p.Temperature, p.DutyPercent);
            var dot = new Ellipse { Width = 10, Height = 10, Fill = accent, Tag = p, Cursor = Cursors.Hand };
            Canvas.SetLeft(dot, c.X - 5); Canvas.SetTop(dot, c.Y - 5);
            Plot.Children.Add(dot);
        }
    }

    private void Plot_SizeChanged(object s, SizeChangedEventArgs e) => Redraw();

    private void Plot_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Ellipse { Tag: CurvePointVm p })
        {
            _dragging = p;
            Plot.CaptureMouse();
        }
    }

    private void Plot_MouseMove(object s, MouseEventArgs e)
    {
        if (_dragging is null || _vm is null) return;
        var (t, d) = FromCanvas(e.GetPosition(Plot));
        var i = _vm.Points.IndexOf(_dragging);
        var lo = i > 0 ? _vm.Points[i - 1].Temperature + 1 : 0;
        var hi = i < _vm.Points.Count - 1 ? _vm.Points[i + 1].Temperature - 1 : 100;
        _dragging.Temperature = Math.Clamp(t, lo, hi);
        var dlo = i > 0 ? _vm.Points[i - 1].DutyPercent : 0;
        var dhi = i < _vm.Points.Count - 1 ? _vm.Points[i + 1].DutyPercent : 100;
        _dragging.DutyPercent = Math.Clamp(d, dlo, dhi);
    }

    private void Plot_MouseLeftButtonUp(object s, MouseButtonEventArgs e)
    {
        _dragging = null;
        Plot.ReleaseMouseCapture();
    }
}
