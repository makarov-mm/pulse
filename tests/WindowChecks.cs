using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pulse.Controls;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;

namespace Pulse;

// A separate entry point for the opt-in Windows checks. The release application
// is built with App.xaml.cs and does not contain this test code.
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        UiText.UsePreferredLanguages(new[] { Environment.GetEnvironmentVariable("PULSE_CHECK_LANGUAGE") ?? "en" });
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            ((FrameworkElement)_window.Content).Loaded += CheckWindow;
            _window.Activate();
        }
        catch (Exception error)
        {
            File.WriteAllText(Environment.GetEnvironmentVariable("PULSE_CHECK_REPORT")!, error.ToString());
            Exit();
        }
    }

    private async void CheckWindow(object sender, RoutedEventArgs args)
    {
        ((FrameworkElement)sender).Loaded -= CheckWindow;
        string path = Environment.GetEnvironmentVariable("PULSE_CHECK_REPORT")!;
        try
        {
            if (Environment.GetEnvironmentVariable("PULSE_EXTENDED_CHECKS") is { Length: > 0 } directory)
                await _window!.RunExtendedAsync(directory);
            else await _window!.RunChecksAsync();
            File.WriteAllText(path, $"PASS {UiText.Language.Name}");
        }
        catch (Exception error) { File.WriteAllText(path, error.ToString()); }
        finally { _window!.Close(); }
    }
}

public sealed partial class MainWindow
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal async Task RunChecksAsync()
    {
        for (int i = 0; i < 100 && _frame is null; i++) await Task.Delay(100);
        var frame = _frame ?? throw new InvalidOperationException("The first process sample did not arrive.");
        PauseButton.IsChecked = true;
        PauseClicked(this, new());
        Require(_paused && frame.Tasks.Any(t => t.Info.Key.Pid == Environment.ProcessId), "Missing live process.");
        Require(SearchBox.PlaceholderText == UiText.Get("Search") && Title == UiText.Get("WindowTitle"), "Wrong UI language.");
        CheckLanguageResources();
        CheckExecutableLocation();
        await CheckDetailIdentityAsync(frame.Tasks);

        var sample = frame with
        {
            Time = frame.Time + 5,
            Disks = new DiskSample[] { new("0 C:", 0, "C:", 1024, 2048, 25, 0.002) },
            Adapters = new AdapterSample[] { new("fixture", "Test adapter", 256, 512, 1_000_000_000) }
        };
        UpdateDevices(sample);
        var opener = (Button)DiskCard.FindName("Expand");
        ((IInvokeProvider)new ButtonAutomationPeer(opener).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(100);
        Require(_deviceView == DeviceView.Disk && DeviceGrid.Children.Count == 1, "The disk view did not open.");
        var tile = _devices[DeviceView.Disk].Tiles.Single().Tile;
        ExpandDevice(DeviceView.Disk, "0 C:");
        await Task.Delay(100);
        Require(ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), tile), "Expanding a tile lost keyboard focus.");
        SpanBox.SelectedIndex = 1;
        Require(((TextBlock)tile.FindName("SpanStart")).Text.Contains(SpanLabel) && _span == 900, "Incorrect expanded time axis.");

        UpdateDevices(sample with { Time = sample.Time + 1, Disks = null, Adapters = Array.Empty<AdapterSample>() });
        Require(_devices[DeviceView.Network].Tiles.Count == 0, "The last disconnected adapter is still displayed.");
        Require(ReferenceEquals(tile, _devices[DeviceView.Disk].Tiles.Single().Tile), "A failed query discarded the disk history.");
        Require(((TextBlock)tile.FindName("Reading")).Text == "—" && ((TextBlock)tile.FindName("Caption")).Text.Length == 0,
            "A missing disk sample still shows old counters.");
        UpdateDevices(sample with { Time = sample.Time + 2 });
        Require(ReferenceEquals(tile, _devices[DeviceView.Disk].Tiles.Single().Tile), "Recovery replaced the disk tile.");

        var size = AppWindow.Size;
        double scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new((int)(585 * scale), (int)(555 * scale)));
        await Task.Delay(200);
        Require(TaskList.ActualHeight >= 36, "Opening devices hid every process row in a short window.");
        double searchRight = SearchBox.TransformToVisual(Root).TransformPoint(new(SearchBox.ActualWidth, 0)).X;
        Require(searchRight <= Root.ActualWidth && Tabs.ActualWidth > 75, "The tab strip pushed search out of the window.");
        AppWindow.Resize(size);
        CloseDevices(this, new());
        await Task.Delay(150);

        var chart = (Sparkline)MemoryCard.FindName("Plot");
        var line = (Microsoft.UI.Xaml.Shapes.Path)chart.FindName("Line");
        var clock = DateTime.Now;
        chart.Clear();
        foreach (int second in new[] { 0, 1, 120, 121 })
            chart.Add(second, clock.AddSeconds(second), 30);
        Require(line.Data is PathGeometry geometry && geometry.Figures.Count == 2, "A line crossed a sampling gap.");
        chart.Clear();
        Require(line.Data is null, "Clearing history left a stale graph on screen.");

        _moments.Clear();
        for (int second = 0; second <= 1000; second++) RecordMoment(sample with { Time = second });
        Require(_moments.Count == 901 && _moments[0].Time == 100, "Unexpected 15-minute retention boundary.");
        CheckMonitorTabs();
        await CheckTableHeadersAsync();
        await CheckDiskSpaceAsync();
        await CheckRoadmapAsync();
        PauseButton.IsChecked = false;
        PauseClicked(this, new());
        Require(!_paused && _moments.Count <= 1, "Resuming retained the paused history.");
    }
    private static void CheckLanguageResources()
    {
        var language = UiText.Language;
        try
        {
            foreach (var (preferences, expected) in new (string[], string)[]
            {
                (["it-IT", "es-MX"], "es"), (["fr-CA"], "fr"), (["uk-UA"], "uk"),
                (["zh-SG"], "zh-Hans"), (["zh"], "zh-Hans"), (["zh-Hant-TW", "de-CH"], "de"), (["it-IT"], "en")
            })
            {
                UiText.UsePreferredLanguages(preferences);
                Require(UiText.Language.Name == expected, "Incorrect preferred-language fallback.");
            }
        }
        finally { UiText.UsePreferredLanguages([language.Name]); }

        var resources = new System.Resources.ResourceManager("Pulse.Localization.Strings", typeof(App).Assembly);
        var neutral = resources.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, false)!;
        var translation = language.Name == "en" ? neutral : resources.GetResourceSet(language, true, false);
        Require(translation != null, $"Missing resource satellite for {language.Name}.");
        static IEnumerable<string> Parameters(string text) =>
            System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+(?:[^{}]*)\}").Select(m => m.Value).Order();
        foreach (System.Collections.DictionaryEntry entry in neutral)
        {
            string key = (string)entry.Key;
            string? value = translation!.GetString(key);
            Require(!string.IsNullOrWhiteSpace(value) && Parameters((string)entry.Value!).SequenceEqual(Parameters(value)),
                $"Missing translation or incorrect parameters for {language.Name}/{key}.");
        }
    }

    private void CheckExecutableLocation()
    {
        var row = TaskList.Items.OfType<ProcessRow>().Single(r => r.Key.Pid == Environment.ProcessId);
        TaskList.SelectedItem = row;
        Require(OpenLocationButton.IsEnabled && !EndButton.IsEnabled &&
            string.Equals(DetailPath.Text, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
            "The inspector did not expose the selected executable.");
        var original = _byPid[row.Key.Pid];
        try
        {
            _byPid[row.Key.Pid] = original with { Info = original.Info with { Key = row.Key with { Created = row.Key.Created + 1 } } };
            UpdateDetails();
            Require(!OpenLocationButton.IsEnabled, "A reused PID left the file location actionable.");
        }
        finally { _byPid[row.Key.Pid] = original; CloseInspector(this, new()); }
    }

    private async Task CheckDetailIdentityAsync(IReadOnlyList<TaskSample> tasks)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var task = tasks.Single(t => t.Info.Key.Pid == process.Id);
        Require(task.Info.BasePriority == process.BasePriority, "The sample did not contain the OS base priority.");
        var key = task.Info.Key;
        Tabs.SelectedItem = DetailsTab;
        for (int i = 0; i < 200 && _identitiesLoading; i++) await Task.Delay(50);
        Require(_identities.ContainsKey(key), "Details did not load the identity of the test process.");
        // A previous sample at a different scheduling priority must trigger a fresh lookup for the same process.
        _identities[key] = (-1, new("", null, "stale"));
        await LoadIdentitiesAsync(tasks);
        Require(_identities[key].BasePriority == task.Info.BasePriority &&
            _identities[key].Identity.User != "stale" &&
            _detailRows.Single(row => row.ProcessId == key.Pid).Priority == Format.Priority(_identities[key].Identity.PriorityClass),
            "A cached identity with an outdated priority remained in the Details tab.");
        Tabs.SelectedItem = ProcessesTab;
    }

    private void CheckMonitorTabs()
    {
        int pid = Environment.ProcessId;
        Tabs.SelectedItem = DetailsTab;
        Require(_detailRows.Any(row => row.ProcessId == pid), "The Details tab lost the test process.");
        Tabs.SelectedItem = UsersTab;
        Require(_userRows.Count != 0, "The Users tab did not list any sessions.");
        Tabs.SelectedItem = ServicesTab;
        if (_serviceReadError is null)
            Require(_serviceRows.Any(row => row.ProcessId > 0), "The Services tab did not show a running service.");
        else
            Require(_serviceRows.Count == 0 && CountText.Text == UiText.Get("ServicesUnavailableShort") &&
                HostedServices(pid) == _serviceReadError, "An unreadable service list was displayed as empty.");
        Tabs.SelectedItem = StartupTab;
        Require(_tab == LowerTab.Startup && CountText.Text == UiText.Format("StartupCount",
            _startupRows.Count(row => row.Item!.Enabled), _startupRows.Count), "Incorrect startup summary.");
        Tabs.SelectedItem = NetworkTab;
        Require(_tab == LowerTab.Network && SearchBox.PlaceholderText == UiText.Get("SearchNetwork"), "Missing network tab.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Tabs.SelectedItem = ConnectionsTab;
        if (_availableTables.HasFlag(SocketTables.Tcp4))
            Require(_connectionRows.Any(row => row.ProcessId == pid && row.Local == listener.LocalEndpoint.ToString()
                && row.State == "LISTENING"), "The Connections tab missed a live local listening socket.");
        if (_availableTables.HasFlag(SocketTables.Udp4))
            Require(_connectionRows.Any(row => row.ProcessId == pid && row.Local == udp.Client.LocalEndPoint!.ToString()
                && row.Protocol == "UDP" && row.Remote == "" && row.State == ""), "Missing or mislabelled live UDP binding.");
        CheckSocketInspection(pid);
    }

    private void CheckSocketInspection(int pid)
    {
        IPAddress local = IPAddress.Loopback, remote = IPAddress.Parse("192.0.2.2");
        TcpConnection listener = new(pid, false, local, 10001, IPAddress.Any, 0, TcpState.Listening);
        TcpConnection udp = new(pid, true, IPAddress.IPv6Loopback, 10002, IPAddress.IPv6None, 0, default) { Udp = true };
        TcpConnection established = new(pid, false, local, 10003, remote, 443, TcpState.Established);
        _connections = [listener, udp, established, established with { LocalPort = 10004 }, udp with { Pid = pid + 1, LocalPort = 10005 }];
        _availableTables = SocketTables.All;
        Require(ProcessEndpoints(pid, bound: true) == $"TCP {listener.Local}{Environment.NewLine}UDPv6 {udp.Local}",
            "Bound ports included an established connection or another process.");
        Require(ProcessEndpoints(pid, bound: false).Split(Environment.NewLine).Length == 2 &&
            ProcessEndpoints(pid, bound: false).Contains($"{established.Local} → {established.Remote}"),
            "Two connections to the same peer were merged or lost their local endpoint.");
        SearchBox.Text = "udPv6";
        UpdateConnectionRows();
        Require(_connectionRows.Count == 2 && _connectionRows.All(row => row.Protocol == "UDPv6"), "Protocol filter failed.");
        SearchBox.Text = "listening";
        UpdateConnectionRows();
        Require(_connectionRows.Count == 1 && _connectionRows[0].Local == listener.Local, "TCP state filter failed.");
        SearchBox.Text = "";

        foreach (var (flag, label) in new[] { (SocketTables.Tcp4, "TCP/IPv4"), (SocketTables.Tcp6, "TCP/IPv6"),
                    (SocketTables.Udp4, "UDP/IPv4"), (SocketTables.Udp6, "UDP/IPv6") })
        {
            _availableTables = SocketTables.All & ~flag;
            Require(WithConnectionStatus("3", SocketTables.All, " · ") == "3 · " + UiText.Format("ProtocolUnavailable", label),
                $"Incorrect availability for {label}.");
        }
        _availableTables = SocketTables.Tcp;
        Require(!ProcessEndpoints(pid, bound: false).Contains("UDP"), "A missing UDP table affected TCP conversations.");
        _availableTables = SocketTables.Udp4;
        Require(ProcessEndpoints(pid, bound: false) == UiText.Get("ConnectionsUnavailable"), "Missing TCP tables were shown as readable.");
        _availableTables = SocketTables.None;
        Require(WithConnectionStatus("0", SocketTables.All, " · ") == UiText.Get("ConnectionsUnavailable"), "Denied queries were counted as empty.");

        // Two adjacent TCP6 address/scope fields in the Windows row layout, both in network byte order.
        nint row = Marshal.AllocHGlobal(56);
        try
        {
            var address = IPAddress.Parse("fe80::1234").GetAddressBytes();
            foreach (var (offset, scope) in new[] { (0, 7), (24, 513) })
            {
                Marshal.Copy(address, 0, row + offset, address.Length);
                Marshal.WriteInt32(row, offset + 16, IPAddress.HostToNetworkOrder(scope));
            }
            Marshal.WriteInt32(row, 20, 0xBB01);
            Marshal.WriteInt32(row, 44, 0x3500);
            Marshal.WriteInt32(row, 48, (int)TcpState.Established);
            Marshal.WriteInt32(row, 52, pid);
            var tcp6 = TcpConnections.ReadRow(row, ipv6: true, udp: false);
            Require(tcp6.Local == "[fe80::1234%7]:443" && tcp6.Remote == "[fe80::1234%513]:53" &&
                tcp6.Pid == pid && tcp6.State == TcpState.Established, "Incorrect TCP6 scope, port, state or owner.");
            Marshal.WriteInt32(row, 24, pid);
            var udp6 = TcpConnections.ReadRow(row, ipv6: true, udp: true);
            Require(udp6.Udp && udp6.Local == tcp6.Local && udp6.Remote == "" && udp6.Pid == pid,
                "Incorrect UDP6 scope, port or owner.");
            Marshal.WriteInt32(row, 0, (int)TcpState.Established);
            Marshal.Copy(local.GetAddressBytes(), 0, row + 4, 4);
            Marshal.WriteInt32(row, 8, 0xBB01);
            Marshal.Copy(remote.GetAddressBytes(), 0, row + 12, 4);
            Marshal.WriteInt32(row, 16, 0x3500);
            Marshal.WriteInt32(row, 20, pid);
            var tcp4 = TcpConnections.ReadRow(row, ipv6: false, udp: false);
            Require(tcp4.Local == "127.0.0.1:443" && tcp4.Remote == "192.0.2.2:53" && tcp4.Pid == pid,
                "Incorrect TCP4 address, port or owner.");
            Marshal.Copy(local.GetAddressBytes(), 0, row, 4);
            Marshal.WriteInt32(row, 4, 0x3500);
            Marshal.WriteInt32(row, 8, pid);
            var udp4 = TcpConnections.ReadRow(row, ipv6: false, udp: true);
            Require(udp4.Protocol == "UDP" && udp4.Local == "127.0.0.1:53" && udp4.Remote == "" && udp4.Pid == pid,
                "Incorrect UDP4 address, port or owner.");
        }
        finally { Marshal.FreeHGlobal(row); }

        ShowProcess(pid);
        Require(Tabs.SelectedItem == ProcessesTab && SearchBox.Text == $"pid:{pid}" &&
            TaskOrder.Build(_frame!.Tasks, SearchBox.Text, SortField.Pid, false, false, []).Single().Task.Info.Key.Pid == pid,
            "Opening a process from another tab did not use an exact PID.");
        SearchBox.Text = "";
    }

    private async Task CheckDiskSpaceAsync()
    {
        Tabs.SelectedItem = SpaceTab;
        Require(_tab == LowerTab.Space && SpaceBrowse.IsEnabled, "Missing disk space tab or folder picker.");
        var fixture = Directory.CreateTempSubdirectory("Pulse-space-");
        try
        {
            var large = fixture.CreateSubdirectory("Nested");
            File.WriteAllBytes(Path.Combine(large.FullName, "payload"), new byte[1024]);
            fixture.CreateSubdirectory("Empty");
            for (int i = 1; i <= 30; i++)
                File.WriteAllBytes(Path.Combine(fixture.FullName, $"file{i}"), new byte[i]);
            File.SetAttributes(Path.Combine(fixture.FullName, "file1"), FileAttributes.Hidden);
            for (int i = 0; i < 65; i++)
            {
                var small = fixture.CreateSubdirectory($"Small{i}");
                File.WriteAllBytes(Path.Combine(small.FullName, "one"), [1]);
            }

            string path = fixture.FullName;
            SelectSpaceTarget(path);
            SelectSpaceTarget(path);
            Require(_spaceTargets.Count(target => target == path) == 1 && _spaceTargets[SpaceDrives.SelectedIndex] == path,
                "Selecting a folder did not set a reusable scan target.");
            await ScanSpaceAsync(path);
            var root = _spaceRoot!;
            Require(root.Size == 1554 && root.Children.Sum(n => n.Size) == 1554, "The scanned file lengths do not add up.");
            var group = root.Children.Single(n => n.Grouped != 0);
            Require(group.Size == 55 && group.Grouped == 10 && root.Children.Count(n => n.IsFile) == 21,
                "The scanner did not keep the 20 largest files and group the rest.");
            Require(SpaceItems.Items.Count == root.Children.Length && _spaceRows.Keys.Any(n => n.Name == "Empty"),
                "The list hid an empty folder or items below the treemap limit.");

            var missing = new SpaceNode(Path.Combine(path, "missing"), null, false);
            SpaceScanner.Scan(missing, CancellationToken.None);
            Require(missing.Unread, "A missing folder was reported as successfully scanned.");
            var unread = new SpaceNode("Unavailable", root, false) { Unread = true };
            root.Children = [..root.Children, unread];
            DrawSpace();
            Require(_spaceRows[unread].Name.Contains(UiText.Get("SpaceUnread")), "An unread folder disappeared from the list.");

            // The largest rectangle must represent its share of all bytes, including the smaller items.
            Root.UpdateLayout();
            DrawSpace();
            var first = SpaceMap.Children.OfType<Border>().First();
            double fraction = (first.Width + 2) * (first.Height + 2) / (SpaceMap.ActualWidth * SpaceMap.ActualHeight);
            Require(Math.Abs(fraction - 1024.0 / root.Size) < 0.001, "The treemap excluded the smaller items from its scale.");

            var nested = root.Children.Single(n => n.Name == "Nested");
            await ClickHeaderAsync(SpaceHeaders, "Name");
            var names = SpaceItems.Items.Cast<SpaceRow>().Select(r => r.Name).ToList();
            Require(names.SequenceEqual(names.Order(StringComparer.CurrentCultureIgnoreCase)), "Disk names did not sort alphabetically.");
            var tile = SpaceMap.Children.OfType<Border>().First();
            Require(Math.Abs(tile.Width - first.Width) < 0.01, "Sorting the list changed the treemap.");
            await ClickHeaderAsync(SpaceHeaders, "Share");
            Require(((SpaceRow)SpaceItems.Items[0]).Node == nested, "Disk share did not sort by byte count.");
            var row = _spaceRows[nested];
            SpaceItems.SelectedItem = row;
            SpaceItems.ScrollIntoView(row);
            SpaceItems.UpdateLayout();
            var folderContainer = (ListViewItem)SpaceItems.ContainerFromItem(row);
            var cells = (Grid)folderContainer.ContentTemplateRoot;
            for (int i = 0; i < 50 && !cells.IsLoaded; i++) await Task.Delay(10);
            Root.UpdateLayout();
            var amount = (TextBlock)cells.Children[2];
            double rightEdge = amount.TransformToVisual(SpaceHeaders).TransformPoint(new(amount.ActualWidth, 0)).X;
            var sizeHeader = (Button)SpaceHeaders.Children[1];
            double headingEdge = sizeHeader.TransformToVisual(SpaceHeaders).TransformPoint(new(sizeHeader.ActualWidth, 0)).X;
            Require(Math.Abs(rightEdge - headingEdge) <= 2, $"Disk size cell ends at {rightEdge} DIP, heading at {headingEdge}; row width {cells.ActualWidth}, header width {SpaceHeaders.ActualWidth}, list width {SpaceItems.ActualWidth}, container width {folderContainer.ActualWidth}.");
            Require(folderContainer.Focus(FocusState.Keyboard), "Could not focus a folder.");
            root.Children.Single(n => n.Name == "Small0").Add(2048);
            DrawSpace();
            Require((FocusManager.GetFocusedElement(Root.XamlRoot) as ListViewItem)?.Content == row &&
                SpaceItems.SelectedItem == row, "Reordering the disk list moved focus away from the folder.");
            OpenSpaceFolder(nested);
            Require(_spaceCurrent == nested && SpaceUp.IsEnabled && SpaceItems.Items.Count == 1, "Folder drill-down failed.");
            SpaceUpClicked(this, new());
            Require(_spaceCurrent == root, "Up did not return to the scanned folder.");

            var scan = ScanSpaceAsync(path);
            SpaceScanClicked(this, new());
            await scan;
            Require(_spaceCancelled && SpacePath.Text.Contains(UiText.Get("SpaceCancelled")) && SpaceBrowse.IsEnabled,
                "Cancelling a scan did not preserve and identify the partial result.");
            await ScanSpaceAsync(path);
            Require(!_spaceCancelled && _spaceRoot?.Size == 1554, "Rescanning did not replace the partial result.");
        }
        finally { fixture.Delete(true); }
        Tabs.SelectedItem = ProcessesTab;
    }

}
