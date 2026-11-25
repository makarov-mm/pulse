using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pulse.Localization;
using Pulse.Telemetry;
using Windows.Foundation;

namespace Pulse.Controls;

public sealed partial class Sparkline : UserControl
{
    private const double WindowSeconds = 60;
    private readonly List<(double Time, double? Value)> _points = [];
    private readonly ToolTip _tip = new();
    private Func<double, string> _format = v => v.ToString("N0");
    private bool _percent;

    public Sparkline()
    {
        InitializeComponent();
        ToolTipService.SetToolTip(Plot, _tip);
        Loaded += (_, _) => Redraw();
    }

    public double AxisMaximum => _percent ? 100 : Math.Max(1, _points.Select(p => p.Value ?? 0).DefaultIfEmpty().Max() * 1.08);

    public void Configure(bool percent, Func<double, string> format)
    {
        _percent = percent;
        _format = format;
    }

    public void Add(double time, double? value, string? detail = null)
    {
        _points.RemoveAll(p => time - p.Time > WindowSeconds || p.Time >= time);
        _points.Add((time, value));
        string text = value is { } v ? _format(v) : UiText.Get("NoData");
        _tip.Content = detail is null ? text : text + Environment.NewLine + detail;
        Redraw();
    }

    private void PlotSizeChanged(object sender, SizeChangedEventArgs args) => Redraw();

    private void Redraw()
    {
        if (!IsLoaded || Plot is null || _points.Count == 0 || Plot.ActualWidth < 2 || Plot.ActualHeight < 2) return;
        double w = Plot.ActualWidth - 2, h = Plot.ActualHeight - 2;
        double max = AxisMaximum;
        var stroke = new PathGeometry();
        var fill = new PathGeometry();
        PointCollection? points = null, fillPoints = null;
        double? lastTime = null;
        void Finish()
        {
            if (points is null) return;
            var edge = new PathFigure { StartPoint = points[0] };
            edge.Segments.Add(new PolyLineSegment { Points = points });
            stroke.Figures.Add(edge);
            var shape = new PathFigure { StartPoint = new(points[0].X, h + 1), IsClosed = true };
            shape.Segments.Add(new PolyLineSegment { Points = fillPoints! });
            shape.Segments.Add(new LineSegment { Point = new(points[points.Count - 1].X, h + 1) });
            fill.Figures.Add(shape);
            points = null;
            fillPoints = null;
        }
        foreach (var p in _points)
        {
            if (lastTime is { } previous && p.Time - previous > Sampler.MaxSampleInterval) Finish();
            lastTime = p.Time;
            if (p.Value is not { } value) { Finish(); continue; }
            double x = 1 + w * (1 - (_points[^1].Time - p.Time) / WindowSeconds);
            var point = new Point(x, 1 + h * (1 - Math.Clamp(value / max, 0, 1)));
            points ??= new PointCollection();
            points.Add(point);
            fillPoints ??= new PointCollection();
            fillPoints.Add(point);
        }
        Finish();
        Line.Data = stroke;
        Area.Data = fill;
    }
}
