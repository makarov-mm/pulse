using Pulse.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Pulse.Controls;

// A history graph. It keeps Retention seconds of samples and draws the most recent span.
// Hovering shows the sample nearest to the pointer and a tap reports it through SampleSelected.
public sealed partial class Sparkline : UserControl
{
    public const double Retention = 900;
    private readonly List<(double Time, DateTime Clock, double? Value, string? Detail)> _history = [];
    private readonly ToolTip _tip = new();
    private double? _pointerX;
    private double _span = 60;
    private Func<double, string> _format = v => v.ToString("N0");
    private bool _percent;

    public Sparkline()
    {
        InitializeComponent();
        ToolTipService.SetToolTip(Plot, _tip);
    }

    // Raised with the sample time when the user taps the graph.
    public event EventHandler<double>? SampleSelected;

    // An extra tooltip line, such as what a tap does.
    public string? Hint { get; set; }

    // The value at the top of the vertical axis. Percentages use a fixed scale.
    // Other values scale to the highest sample in the visible span.
    public double AxisMaximum => _percent ? 100 : Math.Max(1, Visible().Select(p => p.Value ?? 0).DefaultIfEmpty().Max() * 1.08);

    private IEnumerable<(double Time, DateTime Clock, double? Value, string? Detail)> Visible() =>
        _history.Where(p => _history[^1].Time - p.Time <= _span);

    public void SetSpan(double seconds)
    {
        _span = Math.Clamp(seconds, 1, Retention);
        UpdateTooltip();
        Redraw();
    }

    public void Configure(bool percent, Func<double, string> format)
    {
        _percent = percent;
        _format = format;
    }

    public void Add(double time, DateTime clock, double? value, string? detail = null)
    {
        _history.RemoveAll(p => time - p.Time > Retention);
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

    private void PlotTapped(object sender, TappedRoutedEventArgs args)
    {
        if (SampleSelected is null || _history.Count == 0 || Plot.ActualWidth < 1) return;
        double target = TimeAt(args.GetPosition(Plot).X);
        if (target < _history[0].Time - 1) return;
        SampleSelected(this, _history.MinBy(p => Math.Abs(p.Time - target)).Time);
    }

    // Maps a horizontal position to a sample time. The newest sample sits at the right edge.
    private double TimeAt(double x) => _history[^1].Time - _span + _span * x / Plot.ActualWidth;

    private void UpdateTooltip()
    {
        if (_history.Count == 0 || Plot.ActualWidth < 1) return;
        double target = TimeAt(_pointerX ?? Plot.ActualWidth);
        var p = _history.MinBy(p => Math.Abs(p.Time - target));
        string text = target < _history[0].Time - 1 ? UiText.Get("NoSample") :
            $"{p.Clock:T}   {(p.Value is { } v ? _format(v) : UiText.Get("NoData"))}{(p.Detail is null ? "" : Environment.NewLine + p.Detail)}";
        _tip.Content = Hint is null ? text : text + Environment.NewLine + Hint;
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
        foreach (var p in Visible())
        {
            if (p.Value is not { } value) { Finish(); continue; }
            double x = 1 + w * (1 - (_history[^1].Time - p.Time) / _span);
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
