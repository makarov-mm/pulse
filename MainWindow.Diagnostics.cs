using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pulse.Controls;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;
using Frame = Pulse.Telemetry.Frame;

namespace Pulse;

public sealed partial class MainWindow
{
    private readonly ProcessHistory _history = new();
    private readonly List<(double Time, DateTime Clock, IReadOnlyList<GpuDevice>? Devices)> _graphics = [];
    private DiagnosticsWindow? _diagnostics;
    private ILookup<(int Pid, int Port), ActivityRate>? _flowRates;

    internal void UpdateFlows(IEnumerable<ActivityRate>? values)
    {
        if (_closed || (_paused && values != null)) return;
        _flowRates = values?.Where(r => r.Socket != null).ToLookup(r => (r.Pid, r.Socket!.Local.Port));
        if (_paused) return;
        if (_tab == LowerTab.Connections) UpdateConnectionRows();
        if (Inspector.IsPaneOpen && InspectorSection == 3) UpdateDetails();
    }

    private (double? Received, double? Sent) SocketRates(TcpConnection socket)
    {
        if (_flowRates == null || (!socket.Udp && socket.State == TcpState.Listening)) return (null, null);
        double received = 0, sent = 0;
        bool any = false;
        foreach (var value in _flowRates[(socket.Pid, socket.LocalPort)])
        {
            var flow = value.Socket!;
            bool wildcard = socket.Udp && (socket.LocalAddress.Equals(System.Net.IPAddress.Any) ||
                socket.LocalAddress.Equals(System.Net.IPAddress.IPv6Any));
            if (flow.Protocol != (socket.Udp ? "UDP" : "TCP") ||
                flow.Local.AddressFamily != socket.LocalAddress.AddressFamily ||
                (!wildcard && !flow.Local.Address.Equals(socket.LocalAddress)) ||
                (!socket.Udp && (!flow.Remote.Address.Equals(socket.RemoteAddress) || flow.Remote.Port != socket.RemotePort))) continue;
            received += value.Incoming; sent += value.Outgoing; any = true;
        }
        return any ? (received, sent) : (null, null);
    }
    internal IReadOnlyList<HistorySample> Observations => _history.Samples;
    internal IReadOnlyList<(double Time, DateTime Clock, IReadOnlyList<GpuDevice>? Devices)> GraphicsHistory => _graphics;
    internal HistoryCapture Capture() => HistoryCapture.Create(_frame, _history.Samples);
    internal static string GpuText(GpuLoad? gpu) => gpu is null ? "—" : $"{Format.Percent(gpu.Percent)} · {gpu.Adapter} · {gpu.Engine}";

    private void ConfigureDiagnostics()
    {
        InspectorPage.SelectedItem = InspectorPage.Items[0];
        foreach (var card in new[] { CpuCard, MemoryCard, DiskCard, NetworkCard })
        {
            var graph = (Sparkline)card.FindName("Plot");
            graph.EnableRange((a, b) => Diagnostics().UseLive(a, b), t => ShowMoment(card, t));
        }
        Root.RegisterPropertyChangedCallback(FrameworkElement.RequestedThemeProperty,
            (_, _) => _diagnostics?.ApplyTheme(Root.RequestedTheme));
    }

    private DiagnosticsWindow Diagnostics()
    {
        if (_diagnostics is null)
        {
            _diagnostics = new(this);
            _diagnostics.ApplyTheme(Root.RequestedTheme);
            _diagnostics.Closed += (_, _) => _diagnostics = null;
        }
        _diagnostics.Activate();
        return _diagnostics;
    }

    private void RecordDiagnostics(Frame f)
    {
        _history.Add(f, (int)Math.Max(1, _timer.Interval.TotalSeconds));
        _graphics.RemoveAll(g => f.Time - g.Time > 900);
        _graphics.Add((f.Time, f.Clock, f.Gpu?.Devices));
        _diagnostics?.UpdateLive();
    }

    private void DiagnosticsClicked(object sender, RoutedEventArgs args) => Diagnostics();
    private void PinClicked(object sender, RoutedEventArgs args)
    {
        if (_selected is { } key && _byPid.TryGetValue(key.Pid, out var task) && task.Info.Key == key)
            Diagnostics().Pin(key, task.Info.Name);
    }

    private int InspectorSection => InspectorPage?.SelectedItem is { } item ? InspectorPage.Items.IndexOf(item) : 0;

    private void InspectorPageChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (InspectorExtra is null || !Inspector.IsPaneOpen) return;
        RefreshInspectorSources();
        UpdateDetails();
    }

    private void UpdateInspectorPage(ProcessKey key)
    {
        int page = InspectorSection;
        DetailFacts.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        InspectorExtra.Visibility = page == 0 ? Visibility.Collapsed : Visibility.Visible;
        InspectorExtra.Text = page switch
        {
            1 when _frame?.ThreadOwner == key => string.Join(Environment.NewLine,
                _frame.Threads.OrderByDescending(t => t.Cpu).Select(t => $"TID {t.Id}     {Format.Percent(t.Cpu)}")),
            2 => HostedServices(key.Pid),
            3 => WithConnectionStatus(string.Join(Environment.NewLine, _connections.Where(c => c.Pid == key.Pid)
                .Select(c => { var rate = SocketRates(c); return $"{c.Protocol}  {c.Local}  {c.Remote}" + (rate.Received.HasValue ? $"  ↓ {Format.Bytes(rate.Received, true)}  ↑ {Format.Bytes(rate.Sent, true)}" : ""); })), SocketTables.All, Environment.NewLine),
            _ => ""
        };
        if (InspectorExtra.Text.Length == 0) InspectorExtra.Text = "—";
    }
}
