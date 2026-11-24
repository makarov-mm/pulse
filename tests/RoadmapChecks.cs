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
        if (UiText.Language.TwoLetterISOLanguageName == "en") CheckHistoryCapacity();
        var temporary = Directory.CreateTempSubdirectory("Pulse-roadmap-");
        try
        {
            string file = Path.Combine(temporary.FullName, "fixture.pulse");
            var key = new ProcessKey(33, 100);
            var other = new ProcessKey(33, 300);
            ProcessReading Row(ProcessKey id, long n) => new(id, "worker", n, n * 2, n * 3, n * 4, 2, 10, 1);
            HistorySample Sample(int time, params ProcessReading[] rows) => new(time, DateTimeOffset.UnixEpoch.AddSeconds(time), 1, time, time * 100, rows);
            var data = HistoryCapture.Create(_frame, new[] { Sample(0, Row(key, 4)), Sample(1, Row(key, 6)),
                Sample(25, Row(key, 100)), Sample(26, Row(key, 101)), Sample(27, Row(other, 1)), Sample(28, Row(other, 3)) });
            await data.SaveAsync(file);
            var imported = await HistoryCapture.OpenAsync(file);
            var result = imported.Rank(0, 5);
            Require(result.Count == 2 && result[0].First.Key == key && result[0].CpuTicks == 3 && result[0].Read == 9 &&
                result[0].Write == 12 && result[0].PrivateChange == 194 && result[0].Pairs == 2,
                "An interval crossed a coverage gap or combined reused process IDs.");
            var diagnostics = Diagnostics();
            await diagnostics.LoadRecordingAsync(file, false);
            await diagnostics.LoadRecordingAsync(file, true);
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
            var results = (ListView)root.FindName("Ranks");
            Require(results.Items.Count == 2 && ((IntervalDisplay)results.Items[0]).Name.Contains(UiText.Format("MomentExited", "worker (33)")),
                "The offline interval did not retain the exited process.");
            Require(((TextBlock)root.FindName("Comparison")).Text.Contains("Δ CPU"), "The comparison omitted peak differences.");
            string malformed = Path.Combine(temporary.FullName, "invalid.pulse");
            await File.WriteAllTextAsync(malformed, "{\"Version\":2}");
            try { await HistoryCapture.OpenAsync(malformed); throw new InvalidOperationException("Invalid capture loaded."); }
            catch (InvalidDataException) { }
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
            _graphics.Add((_frame.Time + 1, _frame.Clock, gpu.Devices));
            ((SelectorBar)root.FindName("Pages")).SelectedItem = (SelectorBarItem)root.FindName("GpuPage");
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
            var flow = new SocketFlow("TCP", new(local, 1234), new(peer, 443));
            var socket = new TcpConnection(33, false, local, 1234, peer, 443, TcpState.Established);
            var prior = _flowRates;
            try
            {
                _flowRates = new[] { new ActivityRate(true, 33, "fixture", 120, 25, flow) }
                    .ToLookup(r => (r.Pid, r.Socket!.Local.Port));
                Require(SocketRates(socket) == (120d, 25d) &&
                    SocketRates(socket with { Pid = 34 }) == (null, null) &&
                    SocketRates(socket with { RemotePort = 80 }) == (null, null) &&
                    SocketRates(socket with { State = TcpState.Listening }) == (null, null),
                    "Endpoint rates were assigned to the wrong socket.");
            }
            finally { _flowRates = prior; }

            await CheckPreferencesAsync();
        }
        finally { temporary.Delete(true); }
    }

    private void CheckHistoryCapacity()
    {
        var frame = _frame!;
        var first = frame.Tasks[0];
        var many = Enumerable.Range(1, 400).Select(i => first with
        {
            Info = first.Info with { Key = new(i, i), Name = $"worker-{i}" }
        }).ToArray();
        var history = new ProcessHistory();
        for (int i = 0; i <= 1000; i++)
            history.Add(frame with { Time = i, Clock = frame.Clock.AddSeconds(i), Tasks = many }, 1);
        Require(history.Samples.Count == 901 && history.Samples[0].Time == 100 &&
            HistoryCapture.Create(frame, history.Samples).Rank(0, 900).Count == 400,
            "The bounded process history lost contributors outside the top five.");
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
