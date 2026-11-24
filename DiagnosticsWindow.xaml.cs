using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.Storage.Pickers;
using Pulse.Controls;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;

namespace Pulse;

public sealed partial class DiagnosticsWindow : Window
{
    private sealed class Range(HistoryCapture data)
    {
        public HistoryCapture Data = data;
        public int First, Last = data.Samples.Length - 1;
    }
    private readonly MainWindow _owner;
    private readonly Dictionary<string, (MetricCard Card, double Time)> _charts = [];
    private readonly Dictionary<string, StackPanel> _gpuGroups = [];
    private Range? _measured, _reference;
    private Range? Active => Slot.SelectedIndex == 1 ? _reference : _measured;
    private readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _setting, _closed;
    private string _sort = "CPU";
    private bool _descending = true;
    private ProcessKey? _watch;
    private string _watchName = "";
    private ActivityTrace? _trace;
    private readonly DispatcherTimer _traceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<Button, string> _labels = [];

    internal void ApplyTheme(ElementTheme theme)
    {
        Surface.RequestedTheme = theme;
        AppWindow.TitleBar.PreferredTheme = theme == ElementTheme.Dark ? Microsoft.UI.Windowing.TitleBarTheme.Dark : Microsoft.UI.Windowing.TitleBarTheme.Light;
    }

    private void RankViewportChanged(object sender, SizeChangedEventArgs args)
    {
        RankBody.Width = Math.Max(RankBody.MinWidth, args.NewSize.Width);
    }

    internal DiagnosticsWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        Surface.Language = UiText.Language.Name;
        var size = owner.AppWindow.Size;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, size.Height));
        foreach (var b in RankHeaders.Children.OfType<Button>()) _labels[b] = (string)b.Content;
        Elevate.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        _render.Tick += (_, _) => { _render.Stop(); Report(); };
        _traceTimer.Tick += (_, _) => ReadTrace();
        Closed += (_, _) => { _closed = true; _render.Stop(); StopTrace(); };
        Pages.SelectedItem = RangesPage;
        UseLive(null, null);
    }

    internal void UseLive(double? start, double? end)
    {
        _measured = new(_owner.Capture());
        if (_measured.Last >= 0 && start.HasValue && end.HasValue)
        {
            var samples = _measured.Data.Samples;
            int Closest(double t) => Array.IndexOf(samples, samples.MinBy(p => Math.Abs(p.Time - t)));
            _measured.First = Closest(Math.Min(start.Value, end.Value));
            _measured.Last = Closest(Math.Max(start.Value, end.Value));
        }
        Slot.SelectedIndex = 0;
        Pages.SelectedItem = RangesPage;
        BindRange();
    }

    private void BindRange()
    {
        if (From is null || Until is null || Active is not { } range) return;
        _setting = true;
        From.Maximum = Until.Maximum = Math.Max(0, range.Data.Samples.Length - 1);
        From.Value = range.First;
        Until.Value = Math.Max(0, range.Last);
        _setting = false;
        Report();
    }

    private void RangeChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_setting || Until is null || Active is not { Last: >= 0 } range) return;
        _setting = true;
        if (From.Value > Until.Value)
        {
            if (ReferenceEquals(sender, From)) Until.Value = From.Value;
            else From.Value = Until.Value;
        }
        range.First = (int)From.Value;
        range.Last = (int)Until.Value;
        _setting = false;
        _render.Start();
    }
    private void SlotChanged(object sender, SelectionChangedEventArgs args) => BindRange();
    private void LiveClicked(object sender, RoutedEventArgs args) => UseLive(null, null);
    private void RankClicked(object sender, RoutedEventArgs args)
    {
        string column = (string)((Button)sender).Tag;
        _descending = column == _sort ? !_descending : column != "Name";
        _sort = column;
        Report();
    }

    private static (double? Cpu, long? Memory) Peaks(Range r)
    {
        var samples = r.Data.Samples.Skip(r.First).Take(r.Last - r.First + 1);
        return (samples.Max(s => s.CpuPercent), samples.Max(s => s.UsedMemoryBytes));
    }

    private static string Describe(Range r)
    {
        var samples = r.Data.Samples;
        var (cpu, memory) = Peaks(r);
        int gaps = Enumerable.Range(r.First, r.Last - r.First).Count(i => HistoryCapture.Gap(samples[i], samples[i + 1]));
        return UiText.Format("RangeSummary", samples[r.First].Clock.LocalDateTime, samples[r.Last].Clock.LocalDateTime,
            r.Last - r.First + 1, gaps, Format.Percent(cpu), Format.Bytes(memory));
    }
    private static string Delta(double? n, Func<double?, string> format) =>
        n is null ? "—" : (n < 0 ? "−" : n > 0 ? "+" : "") + format(Math.Abs(n.Value));

    private void Report()
    {
        if (Ranks is null) return;
        SaveRange.IsEnabled = Active is { Last: >= 0 };
        foreach (var (button, label) in _labels)
            button.Content = (string)button.Tag == _sort ? label + (_descending ? " ↓" : " ↑") : label;
        if (Active is not { Last: >= 0 } r) { Summary.Text = UiText.Get("NoData"); Comparison.Text = ""; Ranks.ItemsSource = null; return; }
        var samples = r.Data.Samples;
        Summary.Text = Describe(r);
        Comparison.Text = "";
        if (_reference is { Last: >= 0 } b && _measured is { Last: >= 0 } a)
        {
            var peak = Peaks(a); var other = Peaks(b);
            var different = a.Data.Host != b.Data.Host || !a.Data.Samples.Skip(a.First).Take(a.Last-a.First+1).Select(s => s.IntervalSeconds).ToHashSet()
                .SetEquals(b.Data.Samples.Skip(b.First).Take(b.Last-b.First+1).Select(s => s.IntervalSeconds));
            Comparison.Text = UiText.Get(Slot.SelectedIndex == 0 ? "BaseRange" : "MeasuredRange") + " · " +
                Describe(Slot.SelectedIndex == 0 ? b : a) + Environment.NewLine +
                $"Δ CPU {Delta(peak.Cpu - other.Cpu, Format.Percent)}   Δ {UiText.Get("Memory")} {Delta(peak.Memory - other.Memory, n => Format.Bytes(n))}" +
                (different ? "   " + UiText.Get("DifferentCapture") : "");
        }
        IComparable Order(HistoryCapture.Contribution p) => _sort switch
        {
            "Name" => p.First.Name, "Private" => p.PrivateChange, "Read" => p.Read, "Write" => p.Write,
            "Time" => p.FirstIndex, _ => p.CpuTicks
        };
        var ranked = r.Data.Rank(r.First, r.Last);
        var sorted = _descending ? ranked.OrderByDescending(Order) : ranked.OrderBy(Order);
        var alive = samples[r.Last].Processes.Select(p => p.Key).ToHashSet();
        Ranks.ItemsSource = sorted.Select(p =>
        {
            string name = $"{p.First.Name} ({p.First.Key.Pid})";
            return new IntervalDisplay(alive.Contains(p.First.Key) ? name : UiText.Format("MomentExited", name),
                p.Pairs > 0 ? TimeSpan.FromTicks(p.CpuTicks).ToString("g") : "—",
                p.FirstIndex == p.LastIndex ? "—" : Delta(p.PrivateChange, n => Format.Bytes(n)),
                Format.Bytes(p.Pairs > 0 ? p.Read : null), Format.Bytes(p.Pairs > 0 ? p.Write : null),
                UiText.Format("FromTo", samples[p.FirstIndex].Clock.LocalDateTime, samples[p.LastIndex].Clock.LocalDateTime));
        }).ToList();
    }

    private async void SaveClicked(object sender, RoutedEventArgs args)
    {
        if (Active is not { Last: >= 0 } r) return;
        var data = r.Data.Slice(r.First, r.Last);
        try
        {
            var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "MakarovPulse-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
            picker.FileTypeChoices.Add(UiText.Get("CaptureFile"), new List<string> { ".pulse" });
            if (await picker.PickSaveFileAsync() is { } file) await Task.Run(() => data.SaveAsync(file.Path));
        }
        catch (Exception e) { Error(e); }
    }
    private async void OpenClicked(object sender, RoutedEventArgs args) => await OpenAsync(false);
    private async void CompareClicked(object sender, RoutedEventArgs args) => await OpenAsync(true);
    private async Task OpenAsync(bool reference)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".pulse");
            if (await picker.PickSingleFileAsync() is not { } file) return;
            await LoadRecordingAsync(file.Path, reference);
        }
        catch (Exception e) { Error(e); }
    }

    internal async Task LoadRecordingAsync(string path, bool reference)
    {
        var loaded = new Range(await Task.Run(() => HistoryCapture.OpenAsync(path)));
        if (_closed) return;
        if (reference) { _reference = loaded; ReferenceOption.IsEnabled = true; }
        else _measured = loaded;
        Slot.SelectedIndex = reference ? 1 : 0;
        BindRange();
    }

    internal void Pin(ProcessKey key, string name)
    {
        _watch = key; _watchName = name;
        Pages.SelectedItem = WatchPage;
        ResetCharts();
        UpdateLive();
    }

    private void PageChanged(SelectorBar bar, SelectorBarSelectionChangedEventArgs args)
    {
        if (Activity is null) return;
        Ranges.Visibility = bar.SelectedItem == RangesPage ? Visibility.Visible : Visibility.Collapsed;
        Charts.Visibility = bar.SelectedItem == WatchPage || bar.SelectedItem == GpuPage ? Visibility.Visible : Visibility.Collapsed;
        Activity.Visibility = bar.SelectedItem == ActivityPage ? Visibility.Visible : Visibility.Collapsed;
        if (bar.SelectedItem != ActivityPage) StopTrace();
        ResetCharts();
        UpdateLive();
    }

    private void ResetCharts() { _charts.Clear(); _gpuGroups.Clear(); ChartsPanel.Children.Clear(); }
    private void Empty(string text)
    {
        if (ChartsPanel.Children.FirstOrDefault() is TextBlock t) t.Text = text;
        else { ResetCharts(); ChartsPanel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }); }
    }

    private void Plot(string key, string name, bool percent, Func<double?, string> format,
        IEnumerable<(double Time, DateTime Clock, double? Value)> points, string caption, StackPanel? parent = null)
    {
        if (!_charts.TryGetValue(key, out var state))
        {
            if (ChartsPanel.Children.FirstOrDefault() is TextBlock) ChartsPanel.Children.Clear();
            var chart = new MetricCard();
            chart.Configure(name, "\uE9D9", percent, v => format(v));
            chart.SetSpan(900);
            (parent ?? ChartsPanel).Children.Add(chart);
            state = (chart, double.NegativeInfinity);
        }
        var fresh = points.Where(p => p.Time > state.Time).ToList();
        if (fresh.Count > 0)
        {
            var p = fresh[^1];
            string axis = UiText.Format("FromTo", p.Clock.AddMinutes(-15), p.Clock);
            state.Card.SetSeries(fresh, format(p.Value), caption + "   ·   " + axis);
            _charts[key] = (state.Card, p.Time);
        }
    }

    internal void UpdateLive()
    {
        if (_closed || Charts is null || Charts.Visibility != Visibility.Visible) return;
        if (Pages.SelectedItem == WatchPage)
        {
            if (_watch is not { } key) { Empty(UiText.Get("NoPinned")); return; }
            var history = _owner.Observations;
            if (history.Count == 0) return;
            double after = _charts.Count == 0 ? double.NegativeInfinity : _charts.Values.Min(s => s.Time);
            var points = history.Where(f => f.Time > after).Select(f =>
            {
                int i = Array.FindIndex(f.Processes, p => p.Key == key);
                return (f.Time, Clock: f.Clock.LocalDateTime, P: i < 0 ? (ProcessReading?)null : f.Processes[i]);
            }).ToList();
            if (points.Count == 0) return;
            string title = $"{_watchName} ({key.Pid})";
            if (points[^1].P is null) title = UiText.Format("MomentExited", title);
            void Draw(string id, bool percent, Func<ProcessReading, double?> select, Func<double?, string> format) =>
                Plot(id, UiText.Get(id), percent, format, points.Select(p => (p.Time, p.Clock, p.P is { } row ? select(row) : null)), title);
            Draw("CPU", true, r => r.CpuPercent, Format.Percent);
            Draw("PrivateBytes", false, r => r.PrivateBytes, n => Format.Bytes(n));
            Draw("Threads", false, r => r.Threads, n => n?.ToString("N0") ?? "—");
            Draw("Handles", false, r => r.Handles, n => n?.ToString("N0") ?? "—");
        }
        else if (Pages.SelectedItem == GpuPage)
        {
            var frames = _owner.GraphicsHistory;
            if (frames.LastOrDefault().Devices is not { Count: > 0 } devices) { Empty(UiText.Get("NoData")); return; }
            var seen = new HashSet<string>();
            foreach (var d in devices)
            {
                if (!_gpuGroups.TryGetValue(d.Id, out var panel))
                {
                    if (ChartsPanel.Children.FirstOrDefault() is TextBlock) ChartsPanel.Children.Clear();
                    panel = new StackPanel { Spacing = 6 };
                    panel.Children.Add(new TextBlock { Text = d.Name, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
                    _gpuGroups.Add(d.Id, panel);
                    ChartsPanel.Children.Add(panel);
                }
                void Draw(string id, string label, bool percent, Func<GpuDevice, double?> metric)
                {
                    string key = d.Id + "/" + id;
                    seen.Add(key);
                    double after = _charts.TryGetValue(key, out var old) ? old.Time : double.NegativeInfinity;
                    Plot(key, label, percent, n => percent ? Format.Percent(n) : Format.Bytes(n), frames.Where(f => f.Time > after)
                        .Select(f => (f.Time, f.Clock, f.Devices?.FirstOrDefault(v => v.Id == d.Id) is { } data ? metric(data) : null)), d.Name, panel);
                }
                Draw("dedicated", UiText.Get("DedicatedGpu"), false, v => v.Dedicated);
                Draw("shared", UiText.Get("SharedGpu"), false, v => v.Shared);
                foreach (var engine in d.Engines.Keys.Order()) Draw(engine, engine, true, v => v.Engines.GetValueOrDefault(engine));
            }
            foreach (string key in _charts.Keys.Except(seen).ToList())
            {
                var card = _charts[key].Card;
                (card.Parent as Panel)?.Children.Remove(card);
                _charts.Remove(key);
            }
            foreach (string id in _gpuGroups.Keys.Except(devices.Select(d => d.Id)).ToList())
            {
                ChartsPanel.Children.Remove(_gpuGroups[id]);
                _gpuGroups.Remove(id);
            }
        }
    }

    private void Error(Exception e)
    {
        if (_closed) return;
        Problem.Title = UiText.Get("ActionError"); Problem.Message = e.Message; Problem.IsOpen = true;
    }
    private void TraceClicked(object sender, RoutedEventArgs args)
    {
        if (_trace != null) { StopTrace(); return; }
        try { _trace = new(); Problem.IsOpen = false; TraceButton.Content = UiText.Get("StopTrace"); _traceTimer.Start(); }
        catch (Exception e) { Error(e); }
    }
    private void StopTrace()
    {
        _traceTimer.Stop();
        _trace?.Dispose(); _trace = null;
        _owner.UpdateFlows(null);
        TraceButton.Content = UiText.Get("StartTrace");
    }
    private void ReadTrace()
    {
        if (_trace is null) return;
        try
        {
            var (rows, lost) = _trace.Read();
            _owner.UpdateFlows(rows);
            TraceStatus.Text = lost > 0 ? UiText.Format("TraceLost", lost) : rows.Count == 0 ? UiText.Get("NoData") : "";
            Transfers.ItemsSource = rows.Select(r => $"{r.Target}   ·   PID {r.Pid}   ·   {(r.Network ? "↓" : UiText.Get("ReadRate"))} {Format.Bytes(r.Incoming, true)}   {(r.Network ? "↑" : UiText.Get("WriteRate"))} {Format.Bytes(r.Outgoing, true)}").ToList();
        }
        catch (Exception e) { StopTrace(); Error(e); }
    }
    private void ElevateClicked(object sender, RoutedEventArgs args) { try { Elevation.StartElevated(); } catch (Exception e) { Error(e); } }
}
