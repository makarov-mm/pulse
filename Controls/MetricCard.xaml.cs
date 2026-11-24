using Pulse.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Pulse.Controls;

public sealed partial class MetricCard : UserControl
{
    private readonly List<(double Time, DateTime Clock, double? Value, string? Detail)> _history = [];
    private double? _pointerX;
    private Func<double, string> _format = v => v.ToString("N0");
    private bool _percent;
    private readonly ToolTip _tip = new();

    public MetricCard()
    {
        InitializeComponent();
        ToolTipService.SetToolTip(Plot, _tip);
    }

    public void Configure(string title, string glyph, bool percent, Func<double, string> format)
    {
        Heading.Text = title;
        Symbol.Glyph = glyph;
        _percent = percent;
        _format = format;
    }

    public void Update(double time, DateTime clock, double? value, string number, string unit, string caption, string? detail = null)
    {
        _history.RemoveAll(p => time - p.Time > 60);
        _history.Add((time, clock, value, detail));
        Number.Text = number;
        Unit.Text = unit;
        Caption.Text = caption;
        ToolTipService.SetToolTip(Caption, caption);
        UpdateTooltip();
        Redraw();
    }

    public void ClearHistory() => _history.Clear();

    public void SetCompact(bool compact)
    {
        PlotRow.Height = new GridLength(compact ? 17 : 42);
        Caption.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Number.FontSize = compact ? 22 : 29;
    }

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
        double target = _history[^1].Time - 60 + 60 * (_pointerX ?? Plot.ActualWidth) / Plot.ActualWidth;
        var p = _history.MinBy(p => Math.Abs(p.Time - target));
        _tip.Content = target < _history[0].Time - 1 ? UiText.Get("NoSample") :
            $"{p.Clock:T}   {(p.Value is { } v ? _format(v) : UiText.Get("NoData"))}{(p.Detail is null ? "" : Environment.NewLine + p.Detail)}";
    }

    private void Redraw()
    {
        if (Plot is null || _history.Count == 0 || Plot.ActualWidth < 2 || Plot.ActualHeight < 2) return;
        double w = Plot.ActualWidth - 2, h = Plot.ActualHeight - 2;
        double max = _percent ? 100 : Math.Max(1, _history.Max(p => p.Value ?? 0) * 1.08);
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
            double x = 1 + w * (1 - (_history[^1].Time - p.Time) / 60);
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

