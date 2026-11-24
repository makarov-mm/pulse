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
        _window = new MainWindow();
        ((FrameworkElement)_window.Content).Loaded += CheckWindow;
        _window.Activate();
    }

    private async void CheckWindow(object sender, RoutedEventArgs args)
    {
        ((FrameworkElement)sender).Loaded -= CheckWindow;
        string path = Environment.GetEnvironmentVariable("PULSE_CHECK_REPORT")!;
        try
        {
            await _window!.RunChecksAsync();
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
        await CheckDiskSpaceAsync();
        PauseButton.IsChecked = false;
        PauseClicked(this, new());
        Require(!_paused && _moments.Count <= 1, "Resuming retained the paused history.");
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
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        Tabs.SelectedItem = ConnectionsTab;
        if (_ipv4Available)
            Require(_connectionRows.Any(row => row.ProcessId == pid && row.Local == listener.LocalEndpoint.ToString()
                && row.State == "LISTENING"), "The Connections tab missed a live local listening socket.");
        else
            Require(CountText.Text.Contains(UiText.Get("ConnectionsUnavailable")) ||
                CountText.Text.Contains(UiText.Format("ProtocolUnavailable", "IPv4")), "The missing IPv4 table was counted as empty.");

        _ipv4Available = true;
        _ipv6Available = false;
        Require(WithConnectionStatus("3", " · ") == "3 · " + UiText.Format("ProtocolUnavailable", "IPv6"),
            "Partial access hid the available connection count.");
        _ipv4Available = false;
        Require(RemoteEndpoints(pid) == UiText.Get("ConnectionsUnavailable"), "The inspector hid a failed socket query.");
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
            var row = _spaceRows[nested];
            SpaceItems.SelectedItem = row;
            SpaceItems.ScrollIntoView(row);
            SpaceItems.UpdateLayout();
            Require(((ListViewItem)SpaceItems.ContainerFromItem(row)).Focus(FocusState.Keyboard), "Could not focus a folder.");
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
