using System.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Telemetry;
using Frame = Pulse.Telemetry.Frame;

namespace Pulse;

public sealed partial class MainWindow
{
    private DiagnosticsWindow? _diagnostics;
    private IReadOnlyList<ActivityRate>? _flowRates;
    private List<TcpConnection>? _indexedConnections;
    private readonly Dictionary<TcpConnection, (double Received, double Sent)> _socketRates = [];

    internal void UpdateFlows(IEnumerable<ActivityRate>? values)
    {
        if (_closed) return;
        _flowRates = values?.Where(r => r.Socket != null).ToArray();
        _indexedConnections = null;
        if (_tab == LowerTab.Connections) UpdateConnectionRows();
        if (Inspector.IsPaneOpen && InspectorSection == 3) UpdateDetails();
    }

    private (double? Received, double? Sent) SocketRates(TcpConnection socket)
    {
        if (_flowRates is null) return (null, null);
        if (!ReferenceEquals(_indexedConnections, _connections))
        {
            _socketRates.Clear();
            var bindings = _connections.Where(c => c.Udp || c.State != TcpState.Listening)
                .ToLookup(c => (c.Pid, c.Udp, c.LocalPort));

            foreach (var rate in _flowRates)
            {
                if (rate.Socket is not { } flow) continue;
                TcpConnection? match = null;
                foreach (var candidate in bindings[(rate.Pid, flow.Protocol == "UDP", flow.Local.Port)])
                {
                    if (!MatchesFlow(candidate, flow)) continue;
                    // ETW omits interface scopes. Shared or indistinguishable bindings cannot own a rate uniquely.
                    if (match != null) { match = null; break; }
                    match = candidate;
                }

                if (match is null) continue;
                var prior = _socketRates.GetValueOrDefault(match);
                _socketRates[match] = (prior.Received + rate.Incoming, prior.Sent + rate.Outgoing);
            }

            _indexedConnections = _connections;
        }

        return _socketRates.TryGetValue(socket, out var value) ? value : (null, null);
    }

    private static bool MatchesFlow(TcpConnection socket, SocketFlow flow)
    {
        if (flow.Local.AddressFamily != socket.LocalAddress.AddressFamily) return false;
        bool wildcard = socket.Udp && (socket.LocalAddress.Equals(IPAddress.Any) || socket.LocalAddress.Equals(IPAddress.IPv6Any));
        return (wildcard || MatchesAddress(flow.Local.Address, socket.LocalAddress)) &&
            (socket.Udp || (flow.Remote.Port == socket.RemotePort && MatchesAddress(flow.Remote.Address, socket.RemoteAddress)));
    }

    private static bool MatchesAddress(IPAddress traced, IPAddress bound)
    {
        if (traced.Equals(bound)) return true;
        if (traced.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
            bound.AddressFamily != traced.AddressFamily || traced.ScopeId != 0) return false;
        return traced.GetAddressBytes().AsSpan().SequenceEqual(bound.GetAddressBytes());
    }

    internal Frame? CurrentFrame => _frame;
    internal static string GpuText(GpuLoad? gpu) => gpu is null ? "—" : $"{Format.Percent(gpu.Percent)} · {gpu.Adapter} · {gpu.Engine}";

    private void ConfigureDiagnostics()
    {
        InspectorPage.SelectedItem = InspectorPage.Items[0];

        Root.RegisterPropertyChangedCallback(FrameworkElement.RequestedThemeProperty, (_, _) => _diagnostics?.ApplyTheme(Root.RequestedTheme));
    }

    private DiagnosticsWindow Diagnostics()
    {
        if (_diagnostics is null)
        {
            _diagnostics = new DiagnosticsWindow(this);
            _diagnostics.ApplyTheme(Root.RequestedTheme);
            _diagnostics.Closed += (_, _) => _diagnostics = null;
        }

        _diagnostics.Activate();
        return _diagnostics;
    }

    private void DiagnosticsClicked(object sender, RoutedEventArgs args) => Diagnostics();

    private void PinClicked(object sender, RoutedEventArgs args)
    {
        if (_selected is { } key && _byPid.TryGetValue(key.Pid, out var task) && task.Info.Key == key)
        {
            Diagnostics().Pin(key, task.Info.Name);
        }
    }

    private int InspectorSection => InspectorPage?.SelectedItem is { } item ? InspectorPage.Items.IndexOf(item) : 0;

    private void InspectorPageChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (InspectorExtra is null || !Inspector.IsPaneOpen) return;
        RefreshInspectorSources();
        UpdateDetails();
    }

    private string ProcessEndpoints(int pid)
    {
        var lines = _connections.Where(c => c.Pid == pid).Select(c =>
        {
            string ends = c.Remote.Length == 0 ? c.Local : $"{c.Local} → {c.Remote}";
            var rate = SocketRates(c);
            string traffic = rate.Received.HasValue ? $"  ↓ {Format.Bytes(rate.Received, true)}  ↑ {Format.Bytes(rate.Sent, true)}" : "";
            return $"{c.Protocol} {ends}{traffic}";
        });
        return WithConnectionStatus(string.Join(Environment.NewLine, lines), SocketTables.All, Environment.NewLine);
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
            3 => ProcessEndpoints(key.Pid),
            _ => string.Empty
        };

        if (InspectorExtra.Text.Length == 0)
        {
            InspectorExtra.Text = "—";
        }
    }
}
