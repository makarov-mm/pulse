using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;

namespace Pulse;

public sealed partial class MainWindow
{
    private async Task CheckRoadmapAsync()
    {
        if (UiText.Language.TwoLetterISOLanguageName == "en")
        {
            CheckServiceRecovery();
            string name = "Pulse-test-" + Guid.NewGuid().ToString("N");
            using (ActivityTrace.AcquireOwner(name))
            {
                try { using var otherOwner = ActivityTrace.AcquireOwner(name); throw new Exception("Two trace owners acquired the same session."); }
                catch (InvalidOperationException error) when (error.Message == UiText.Get("TraceInUse")) { }
            }
            using var nextOwner = ActivityTrace.AcquireOwner(name);
        }
        var temporary = Directory.CreateTempSubdirectory("Pulse-roadmap-");
        try
        {
            var diagnostics = Diagnostics();
            var root = (FrameworkElement)diagnostics.Content;
            var originalTheme = Root.RequestedTheme;
            try
            {
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    Root.RequestedTheme = theme;
                    for (int i = 0; i < 40 && (root.ActualTheme != theme || Root.ActualTheme != theme); i++) await Task.Delay(50);
                    Require(Root.ActualTheme == theme && root.ActualTheme == theme,
                        $"Expected {theme}, main {Root.RequestedTheme}/{Root.ActualTheme}, diagnostics {root.RequestedTheme}/{root.ActualTheme}.");
                }
            }
            finally { Root.RequestedTheme = originalTheme; }
            var task = _frame!.Tasks.First(t => t.Info.Key.Pid == Environment.ProcessId);
            diagnostics.Pin(task.Info.Key, task.Info.Name);
            var panels = (StackPanel)root.FindName("ChartsPanel");
            Require(panels.Children.Count == 4, "The pinned process lacks a metric.");
            const string prefix = "luid_0x00000000_0x00000001_phys_0";
            var gpu = GpuCounters.Read(new() { ["pid_33_" + prefix + "_eng_0_engtype_3D"] = 17,
                ["pid_34_" + prefix + "_eng_0_engtype_3D"] = 24, ["pid_33_" + prefix + "_eng_4_engtype_VideoDecode"] = 82 },
                new() { [prefix] = 8192 }, new() { [prefix] = 4096 }, new Dictionary<ulong, string> { [1] = "Fixture GPU" })!;
            Require(gpu.Devices.Single().Engines["3D #0"] == 41 && gpu.Processes[33].Percent == 82 && gpu.Processes[33].Adapter == "Fixture GPU",
                "GPU engine attribution is incorrect.");
            ((SelectorBar)root.FindName("Pages")).SelectedItem = (SelectorBarItem)root.FindName("GpuPage");
            diagnostics.UpdateLive(_frame with { Time = _frame.Time + 1, Gpu = gpu });
            Require(panels.Children.OfType<StackPanel>().Single().Children.Count == 5, "Missing GPU memory or engine charts.");
            diagnostics.Close();
            var blob = Path.Combine(temporary.FullName, "allocation.bin");
            await File.WriteAllBytesAsync(blob, new byte[8193]);
            var disk = new SpaceNode(temporary.FullName, null, false);
            SpaceScanner.Scan(disk, default);
            var item = disk.Children.Single(n => n.Name == "allocation.bin");
            Require(item.Size == 8193 && item.Allocated == FileAllocation.Read(blob) && item.Allocated.HasValue, "Missing allocated size.");

            var local = System.Net.IPAddress.Loopback;
            var peer = System.Net.IPAddress.Parse("192.0.2.10");
            var accumulator = new ActivityAccumulator();
            accumulator.Socket(33, false, local, 1234, peer, 443, 25, false);
            accumulator.Socket(33, false, local, 1234, peer, 443, 120, true);
            accumulator.Socket(33, true, local, 1234, peer, 443, 7, false);
            accumulator.Socket(33, true, peer, 443, local, 1234, 3, true);
            var (rates, lost) = accumulator.Read(1);
            Require(lost == 0 && rates.Count == 2 &&
                rates.All(r => r.Socket!.Local.Address.Equals(local) && r.Socket.Local.Port == 1234) &&
                rates.Any(r => r.Socket!.Protocol == "TCP" && r.Incoming == 120 && r.Outgoing == 25) &&
                rates.Any(r => r.Socket!.Protocol == "UDP" && r.Incoming == 3 && r.Outgoing == 7) &&
                accumulator.Read(1).Rates.Count == 0, "Incoming and outgoing ETW events did not share a socket.");
            var flow = new SocketFlow("TCP", new(local, 1234), new(peer, 443));
            var socket = new TcpConnection(33, false, local, 1234, peer, 443, TcpState.Established);
            var prior = _flowRates;
            var priorConnections = _connections;
            try
            {
                var udp = socket with { Udp = true, LocalAddress = System.Net.IPAddress.Any };
                var wrongPort = udp with { LocalPort = flow.Remote.Port };
                var scoped = System.Net.IPAddress.Parse("fe80::1%7");
                var ipv6Peer = System.Net.IPAddress.Parse("2001:db8::10");
                var scopedSocket = socket with { IPv6 = true, LocalAddress = scoped, RemoteAddress = ipv6Peer };
                var unscopedFlow = flow with { Local = new(new System.Net.IPAddress(scoped.GetAddressBytes()), 1234), Remote = new(ipv6Peer, 443) };
                _connections = [socket, udp, wrongPort, scopedSocket];
                _flowRates = new ActivityRate[] { new(true, 33, "", 120, 25, flow),
                    new(true, 33, "", 3, 7, flow with { Protocol = "UDP" }), new(true, 33, "", 2, 4, unscopedFlow) };
                Require(SocketRates(socket) == (120d, 25d) && SocketRates(udp) == (3d, 7d) &&
                    SocketRates(wrongPort) == (null, null) && SocketRates(scopedSocket) == (2d, 4d) &&
                    SocketRates(socket with { Pid = 34 }) == (null, null) &&
                    SocketRates(socket with { RemotePort = 80 }) == (null, null) &&
                    SocketRates(socket with { State = TcpState.Listening }) == (null, null),
                    "Endpoint rates were assigned to the wrong socket.");
                _connections = [scopedSocket, scopedSocket with { LocalAddress = System.Net.IPAddress.Parse("fe80::1%8") }];
                Require(SocketRates(scopedSocket) == (null, null), "Ambiguous IPv6 scopes duplicated traffic.");
            }
            finally { _flowRates = prior; _connections = priorConnections; _indexedConnections = null; }

            await CheckPreferencesAsync();
        }
        finally { temporary.Delete(true); }
    }

    private static void CheckServiceRecovery()
    {
        var operations = new List<string>();
        void Stop(string name, Action accepted)
        {
            operations.Add("-" + name);
            if (name != "already-stopped") accepted();
        }

        var graph = ServiceControl.StopOrder("root", parent => parent switch
        {
            "root" => ["left", "right"], "left" or "right" => ["shared"], _ => []
        });
        Require(graph.SequenceEqual(new[] { "shared", "left", "right" }), "A transitive service was missed or stopped twice.");
        string[] dependents = ["leaf", "already-stopped", "branch"];
        try
        {
            ServiceControl.ApplyStops("root", Array.Empty<string>(), dependents, false, Stop, _ => { });
            throw new Exception("An unconfirmed dependent was accepted.");
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1051)
        {
            Require(operations.Count == 0, "Services changed before validating the confirmed list.");
        }
        ServiceControl.ApplyStops("root", dependents, dependents, true, Stop, name => operations.Add("+" + name));
        Require(operations.SequenceEqual(new[] { "-leaf", "-already-stopped", "-branch", "-root", "+root", "+branch", "+leaf" }),
            "Restart changed a previously stopped service or restored services out of order.");

        operations.Clear();
        try
        {
            ServiceControl.ApplyStops("root", dependents, dependents, false, (name, accepted) =>
            {
                if (name == "root") throw new IOException("root stop failed");
                Stop(name, accepted);
            }, name =>
            {
                operations.Add("+" + name);
                if (name == "branch") throw new IOException("branch start failed");
            });
            throw new Exception("Service control hid a failed stop.");
        }
        catch (InvalidOperationException error)
        {
            Require(error.Message.Contains("root stop failed") && error.Message.Contains("branch start failed") &&
                operations.TakeLast(2).SequenceEqual(new[] { "+branch", "+leaf" }), "Rollback skipped a service or lost an error.");
        }
    }

    private async Task CheckPreferencesAsync()
    {
        Tabs.SelectedItem = DetailsTab;
        await Task.Delay(80);
        var t = _tables[LowerTab.Details];
        Reorder(t, "WorkingSet", "Pid"); Reorder(t, "CommandLine", "Pid");
        Resize(t, "Name", 324, true);
        t.Settings = t.Settings with { Pin = true, Columns = t.Settings.Columns.Select(c => c.Key == "Threads" ? c with { Hidden = true } : c).ToArray() };
        ApplyTable(t);
        TabSortClicked(t.Buttons.Single(b => (string)b.Tag == "WorkingSet"), new());
        t.Scroll.ChangeView(280, null, null, true);
        await Task.Delay(120);
        Root.UpdateLayout();
        Require(t.Scroll.HorizontalOffset > 100 && Math.Abs(t.Buttons[0].Translation.X - t.Scroll.HorizontalOffset) < 2, "The name did not stay pinned.");
        Require(t.Rows.Count > 0 && t.Rows.All(g => _gridColumns.GetValue(g, row => new(row)).Frozen.Any(e => e.Translation.X > 100)),
            "A virtualized process name moved while its header was pinned.");
        var reopen = new MainWindow();
        try
        {
            var loaded = reopen._tables[LowerTab.Details];
            Require(loaded.Settings.Pin && loaded.Settings.Columns[0].Width == 324 &&
                loaded.Settings.Columns[1].Key == "WorkingSet" && loaded.Settings.Columns[2].Key == "CommandLine" &&
                loaded.Settings.Columns.Single(c => c.Key == "Threads").Hidden && reopen.CurrentOrder(LowerTab.Details) == CurrentOrder(LowerTab.Details),
                "Table settings did not survive reopening.");
            Require(!reopen._tables[LowerTab.Services].Settings.Pin, "Details overwrote another tab's preferences.");
        }
        finally { reopen.Close(); }
        t.Settings = new([], false, null, false);
        _tabOrders.Remove(t.Tab);
        ApplyTable(t); Save(t); UpdateTabHeaders();
        Tabs.SelectedItem = ProcessesTab;
    }
}
