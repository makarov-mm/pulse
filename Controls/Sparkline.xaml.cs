using Pulse.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Pulse.Controls;

// A one-minute history graph. Hovering shows the sample nearest to the pointer.
public sealed partial class Sparkline : UserControl
{
    private const double Span = 60;
    private readonly List<(double Time, DateTime Clock, double? Value, string? Detail)> _history = [];
    private readonly ToolTip _tip = new();
    private double? _pointerX;
    private Func<double, string> _format = v => v.ToString("N0");
    private bool _percent;

    public Sparkline()
    {
        InitializeComponent();
        ToolTipService.SetToolTip(Plot, _tip);
    }

    // The value at the top of the vertical axis. Percentages use a fixed scale.
    // Other values scale to the highest sample in the visible minute.
    public double AxisMaximum =>
        _percent ? 100 : Math.Max(1, _history.Select(p => p.Value ?? 0).DefaultIfEmpty().Max() * 1.08);

    public void Configure(bool percent, Func<double, string> format)
    {
        _percent = percent;
        _format = format;
    }

    public void Add(double time, DateTime clock, double? value, string? detail = null)
    {
        _history.RemoveAll(p => time - p.Time > Span);
        _history.Add((time, clock, value, detail));
        UpdateTooltip();
        Redraw();
    }

    public void Clear() => _history.Clear();

    private void PlotSizeChanged(object sender, SizeChangedEventArgs args) => Redraw();

    private void PlotPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        _pointerX = args.GetCurrentPoint(Plot).Position.X;
        UpdateTooltip();
    }

    private void PlotPointerExited(object sender, PointerRoutedEventArgs args) => _pointerX = null;

    private void UpdateTooltip()
    {
        if (_history.Count == 0 || Plot.ActualWidth < 1) return;
        double target = _history[^1].Time - Span + Span * (_pointerX ?? Plot.ActualWidth) / Plot.ActualWidth;
        var p = _history.MinBy(p => Math.Abs(p.Time - target));
        _tip.Content = target < _history[0].Time - 1 ? UiText.Get("NoSample") :
            $"{p.Clock:T}   {(p.Value is { } v ? _format(v) : UiText.Get("NoData"))}{(p.Detail is null ? "" : Environment.NewLine + p.Detail)}";
    }

    private void Redraw()
    {
        if (Plot is null || _history.Count == 0 || Plot.ActualWidth < 2 || Plot.ActualHeight < 2) return;
        double w = Plot.ActualWidth - 2, h = Plot.ActualHeight - 2;
        double max = AxisMaximum;
        var stroke = new PathGeometry();
        var fill = new PathGeometry();
        PathFigure? edge = null, shape = null;
        double lastX = 0;
        void Finish()
        {
            if (shape is not null) shape.Segments.Add(new LineSegment { Point = new(lastX, h + 1) });
            edge = null;
            shape = null;
        }
        foreach (var p in _history)
        {
            if (p.Value is not { } value) { Finish(); continue; }
            double x = 1 + w * (1 - (_history[^1].Time - p.Time) / Span);
            var point = new Point(x, 1 + h * (1 - Math.Clamp(value / max, 0, 1)));
            if (edge is null)
            {
                edge = new() { StartPoint = point };
                stroke.Figures.Add(edge);
                shape = new() { StartPoint = new(x, h + 1), IsClosed = true };
                fill.Figures.Add(shape);
            }
            edge.Segments.Add(new LineSegment { Point = point });
            shape!.Segments.Add(new LineSegment { Point = point });
            lastX = x;
        }
        Finish();
        Line.Data = stroke;
        Area.Data = fill;
    }
}
