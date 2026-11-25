using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pulse.Controls;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;
using Frame = Pulse.Telemetry.Frame;

namespace Pulse;

public sealed partial class DiagnosticsWindow : Window
{
    private readonly MainWindow _owner;
    private readonly Dictionary<string, MetricCard> _charts = [];
    private readonly Dictionary<string, StackPanel> _gpuGroups = [];
    private bool _closed;
    private ProcessKey? _watch;
    private string _watchName = "";
    private ActivityTrace? _trace;
    private readonly DispatcherTimer _traceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<string> _transfers = [];

    internal void ApplyTheme(ElementTheme theme)
    {
        Surface.RequestedTheme = theme;
        AppWindow.TitleBar.PreferredTheme = theme == ElementTheme.Dark ? Microsoft.UI.Windowing.TitleBarTheme.Dark : Microsoft.UI.Windowing.TitleBarTheme.Light;
    }

    internal DiagnosticsWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        Surface.Language = UiText.Language.Name;
        var size = owner.AppWindow.Size;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, size.Height));
        Elevate.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        _traceTimer.Tick += (_, _) => ReadTrace();
        Transfers.ItemsSource = _transfers;
        Closed += (_, _) => { _closed = true; StopTrace(); };
        Pages.SelectedItem = GpuPage;
    }

    internal void Pin(ProcessKey key, string name)
    {
        _watch = key; _watchName = name;
        Pages.SelectedItem = WatchPage;
        ResetCharts();
        UpdateLive(_owner.CurrentFrame);
    }

    private void PageChanged(SelectorBar bar, SelectorBarSelectionChangedEventArgs args)
    {
        if (Activity is null) return;
        bool activity = bar.SelectedItem == ActivityPage;
        Charts.Visibility = activity ? Visibility.Collapsed : Visibility.Visible;
        Activity.Visibility = activity ? Visibility.Visible : Visibility.Collapsed;
        Problem.IsOpen = false;
        if (activity) StartTrace();
        else StopTrace();
        ResetCharts();
        UpdateLive(_owner.CurrentFrame);
    }

    private void ResetCharts() { _charts.Clear(); _gpuGroups.Clear(); ChartsPanel.Children.Clear(); }
    private void Empty(string text)
    {
        if (ChartsPanel.Children.FirstOrDefault() is TextBlock t) t.Text = text;
        else { ResetCharts(); ChartsPanel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }); }
    }

    private void Plot(string key, string label, bool percent, Func<double?, string> format,
        double time, double? value, string caption, StackPanel? parent = null)
    {
        if (!_charts.TryGetValue(key, out var card))
        {
            if (ChartsPanel.Children.FirstOrDefault() is TextBlock) ChartsPanel.Children.Clear();
            card = new MetricCard();
            card.Configure(label, "\uE9D9", percent, v => format(v));
            _charts.Add(key, card);
            (parent ?? ChartsPanel).Children.Add(card);
        }
        card.Update(time, value, format(value), "", caption);
    }

    internal void UpdateLive(Frame? frame)
    {
        if (_closed || frame is null || Charts is null || Charts.Visibility != Visibility.Visible) return;
        if (Pages.SelectedItem == WatchPage)
        {
            if (_watch is not { } key) { Empty(UiText.Get("NoPinned")); return; }
            var task = frame.Tasks.FirstOrDefault(t => t.Info.Key == key);
            string title = $"{_watchName} ({key.Pid})";
            if (task is null) { Empty(UiText.Format("MomentExited", title)); return; }
            Plot("CPU", UiText.Get("CPU"), true, Format.Percent, frame.Time, task.Cpu, title);
            Plot("PrivateBytes", UiText.Get("PrivateBytes"), false, n => Format.Bytes(n), frame.Time, task.Info.PrivateBytes, title);
            Plot("Threads", UiText.Get("Threads"), false, n => n?.ToString("N0") ?? "—", frame.Time, task.Info.Threads, title);
            Plot("Handles", UiText.Get("Handles"), false, n => n?.ToString("N0") ?? "—", frame.Time, task.Info.Handles, title);
        }
        else if (Pages.SelectedItem == GpuPage)
        {
            if (frame.Gpu?.Devices is not { Count: > 0 } devices) { Empty(UiText.Get("NoData")); return; }
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
                    Plot(key, label, percent, n => percent ? Format.Percent(n) : Format.Bytes(n),
                        frame.Time, metric(d), d.Name, panel);
                }
                Draw("dedicated", UiText.Get("DedicatedGpu"), false, v => v.Dedicated);
                Draw("shared", UiText.Get("SharedGpu"), false, v => v.Shared);
                foreach (var engine in d.Engines.Keys.Order()) Draw(engine, engine, true, v => v.Engines.GetValueOrDefault(engine));
            }
            foreach (string key in _charts.Keys.Except(seen).ToList())
            {
                var card = _charts[key];
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
    private void StartTrace()
    {
        if (_trace is not null) return;
        try { _trace = new ActivityTrace(); _traceTimer.Start(); }
        catch (Exception error) { Error(error); }
    }

    private void StopTrace()
    {
        _traceTimer.Stop();
        var trace = _trace;
        _trace = null;
        try { trace?.Dispose(); }
        catch (Exception error) { if (!_closed) Error(error); }
        _transfers.Clear();
        TraceStatus.Text = "";
        _owner.UpdateFlows(null);
    }
    private void ReadTrace()
    {
        if (_trace is null) return;
        try
        {
            var (rows, lost) = _trace.Read();
            _owner.UpdateFlows(rows);
            TraceStatus.Text = lost > 0 ? UiText.Format("TraceLost", lost) : rows.Count == 0 ? UiText.Get("NoData") : "";
            // Lines are replaced in place so the list keeps its scroll position between readings.
            for (int i = 0; i < rows.Count; i++)
            {
                ActivityRate r = rows[i];
                string line = $"{r.Target}   ·   PID {r.Pid}   ·   {(r.Network ? "↓" : UiText.Get("ReadRate"))} {Format.Bytes(r.Incoming, true)}   {(r.Network ? "↑" : UiText.Get("WriteRate"))} {Format.Bytes(r.Outgoing, true)}";
                if (i == _transfers.Count) _transfers.Add(line);
                else if (_transfers[i] != line) _transfers[i] = line;
            }
            while (_transfers.Count > rows.Count) _transfers.RemoveAt(_transfers.Count - 1);
        }
        catch (Exception e) { StopTrace(); Error(e); }
    }
    private void ElevateClicked(object sender, RoutedEventArgs args) => _owner.RestartElevated();
}
