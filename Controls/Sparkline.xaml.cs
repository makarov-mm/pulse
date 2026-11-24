using Pulse.Localization;
using Pulse.Telemetry;
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
        Loaded += (_, _) => { UpdateTooltip(); Redraw(); };
    }

    // Raised with the sample time when the user taps the graph.
    public event System.EventHandler<double>? SampleSelected;

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

    public void Clear()
    {
        _history.Clear();
        Line.Data = null;
        Area.Data = null;
        _tip.Content = UiText.Get("NoSample");
    }

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
        int index = SampleAt(TimeAt(args.GetPosition(Plot).X));
        if (index >= 0 && _history[index].Value.HasValue) SampleSelected(this, _history[index].Time);
    }

    // Maps a horizontal position to a sample time. The newest sample sits at the right edge.
    private double TimeAt(double x) => _history[^1].Time - _span + _span * x / Plot.ActualWidth;

    // Restrict selection to the visible time window. Inside a long gap only the
    // measured endpoints (within one second) are selectable.
    private int SampleAt(double time)
    {
        if (_history.Count == 0 || time < _history[0].Time - 1) return -1;
        int low = 0, high = _history.Count - 1;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (_history[mid].Time < time) low = mid + 1;
            else high = mid;
        }
        if (low == 0) return 0;
        double before = _history[low - 1].Time, after = _history[low].Time;
        if (after - before > Sampler.MaxSampleInterval && time - before > 1 && after - time > 1) return -1;
        return before >= _history[^1].Time - _span && time - before < after - time ? low - 1 : low;
    }

    private void UpdateTooltip()
    {
        if (!IsLoaded || _history.Count == 0 || Plot.ActualWidth < 1) return;
        int index = _pointerX is { } x ? SampleAt(TimeAt(x)) : _history.Count - 1;
        string text = UiText.Get("NoSample");
        if (index >= 0)
        {
            var p = _history[index];
            text = $"{p.Clock:T}   {(p.Value is { } v ? _format(v) : UiText.Get("NoData"))}{(p.Detail is null ? "" : Environment.NewLine + p.Detail)}";
        }
        _tip.Content = Hint is null ? text : text + Environment.NewLine + Hint;
    }

    private void Redraw()
    {
        if (!IsLoaded || Plot is null || _history.Count == 0 || Plot.ActualWidth < 2 || Plot.ActualHeight < 2) return;
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
        foreach (var p in Visible())
        {
            if (lastTime is { } previous && p.Time - previous > Sampler.MaxSampleInterval) Finish();
            lastTime = p.Time;
            if (p.Value is not { } value) { Finish(); continue; }
            double x = 1 + w * (1 - (_history[^1].Time - p.Time) / _span);
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
