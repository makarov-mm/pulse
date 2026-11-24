using System.Net;
using NetworkInterfaceType = System.Net.NetworkInformation.NetworkInterfaceType;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;

namespace Pulse;

public sealed partial class MainWindow
{
    static partial void OverridePreferencesDirectory(ref string directory) => directory = Environment.GetEnvironmentVariable("PULSE_CHECK_REPORT")! + ".tables";

    private async Task ClickHeaderAsync(Grid headers, string key)
    {
        var button = headers.Children.OfType<Button>().Single(b => Equals(b.Tag, key));
        var previous = CurrentOrder(_tab);
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        for (int i = 0; i < 50 && CurrentOrder(_tab) == previous; i++) await Task.Delay(10);
        var next = CurrentOrder(_tab);
        Require(next.Column == key && previous != next, $"The {_tab}/{key} header did not change the sort.");
        var label = _columnLabels[button].Label;
        Require(AutomationProperties.GetName(button) == UiText.Format(next.Descending ? "SortDescending" : "SortAscending", label),
            $"The {_tab}/{key} sort direction was not announced.");
    }

    private void CheckHeadingWidth(Button button)
    {
        var caption = new TextBlock { Text = (string)button.Content, FontFamily = button.FontFamily,
            FontWeight = button.FontWeight, FontSize = button.FontSize };
        caption.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        double available = button.ActualWidth - button.Padding.Left - button.Padding.Right;
        Require(caption.DesiredSize.Width <= available + 1,
            $"The {_tab}/{button.Tag} heading needs {caption.DesiredSize.Width} DIP, has {available}.");
    }

    private async Task CheckTableHeadersAsync()
    {
        var size = AppWindow.Size;
        Tabs.SelectedItem = ProcessesTab;
        var savedSort = _sort;
        bool savedDirection = _descending;
        foreach (var header in new[] { NameHeader, PidHeader, CpuHeader, RamHeader, IoHeader, ThreadsHeader, GpuHeader })
        {
            var field = Enum.Parse<SortField>((string)header.Tag);
            var invoke = (IInvokeProvider)new ButtonAutomationPeer(header).GetPattern(PatternInterface.Invoke);
            for (int click = 0; click < 2; click++)
            {
                var old = (_sort, _descending);
                invoke.Invoke();
                for (int i = 0; i < 50 && old == (_sort, _descending); i++) await Task.Delay(10);
                Require(_sort == field && old != (_sort, _descending), $"The Processes/{field} header did not sort.");
            }
            CheckHeadingWidth(header);
        }
        _sort = savedSort;
        _descending = savedDirection;
        UpdateHeaders();
        double scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new((int)(585 * scale), (int)(555 * scale)));
        await Task.Delay(120);
        foreach (var (tab, headers, minimum) in new[] { (DetailsTab, DetailsHeaders, 260), (UsersTab, UsersHeaders, 200),
            (ConnectionsTab, ConnectionsHeaders, 140), (NetworkTab, NetworkHeaders, 160), (StartupTab, StartupHeaders, 140),
            (ServicesTab, ServicesHeaders, 140), (SpaceTab, SpaceHeaders, 0) })
        {
            Tabs.SelectedItem = tab;
            Root.UpdateLayout();
            Require(headers.ColumnDefinitions[0].ActualWidth >= minimum, $"The first {_tab} column is {headers.ColumnDefinitions[0].ActualWidth} DIP, expected at least {minimum}.");
            foreach (var button in headers.Children.OfType<Button>())
            {
                string key = (string)button.Tag;
                await ClickHeaderAsync(headers, key);
                var order = CurrentOrder(_tab);
                await ClickHeaderAsync(headers, key);
                Require(CurrentOrder(_tab).Descending != order.Descending, $"The {_tab}/{key} header did not reverse.");
                CheckHeadingWidth(button);
            }
        }
        Require(Grid.GetRow(SpaceListPanel) == 1, "The narrow disk list was squeezed beside the treemap.");
        Tabs.SelectedItem = DetailsTab;
        Root.UpdateLayout();
        var listItem = (ListViewItem)DetailList.ContainerFromIndex(0);
        var grid = (Grid)listItem.ContentTemplateRoot;
        Require(grid.ColumnDefinitions[0].ActualWidth >= 260, "The data cells lost their minimum Name width.");
        for (int i = 0; i < grid.ColumnDefinitions.Count; i++)
        {
            double difference = Math.Abs(grid.ColumnDefinitions[i].ActualWidth - DetailsHeaders.ColumnDefinitions[i].ActualWidth);
            Require(difference <= 2, $"Details column {i} differs by {difference} DIP between its header and rows.");
        }
        var scroller = (ScrollViewer)DetailsView.Children[0];
        scroller.ChangeView(scroller.ScrollableWidth, null, null, true);
        await Task.Delay(60);
        double right = DetailsHeaders.Children.OfType<Button>().Last().TransformToVisual(scroller).TransformPoint(new(0, 0)).X;
        Require(scroller.HorizontalOffset > 0 && right >= 0 && right < scroller.ActualWidth, "The last Details column is unreachable.");
        scroller.ChangeView(0, null, null, true);
        AppWindow.Resize(size);
        await Task.Delay(100);

        var sample = _frame!.Tasks.First();
        TaskSample Sample(int pid, long bytes, double? cpu, uint session) => sample with
        {
            Info = sample.Info with { Key = new(pid, sample.Info.Key.Created), WorkingSet = bytes, Session = session }, Cpu = cpu
        };
        var tasks = new[] { Sample(20, 900, 2, 2), Sample(3, 1024 * 1024, 90, 10), Sample(100, 4096, null, 1) };
        // Sorting fixture data should not query live process metadata for the synthetic identifiers.
        Tabs.SelectedItem = UsersTab;
        _tabOrders[LowerTab.Users] = new("Memory", true);
        UpdateUserRows(tasks);
        Require(_userRows.Select(r => r.Session).SequenceEqual(new[] { "10", "1", "2" }), "Sessions sorted formatted memory as text.");
        _tabOrders[LowerTab.Details] = new("Cpu", false);
        var byCpu = SortItems(LowerTab.Details, tasks, DetailSortValue).Select(t => t.Info.Key.Pid);
        Require(byCpu.SequenceEqual(new[] { 20, 3, 100 }), "Unknown CPU appeared first in ascending order.");
        _tabOrders[LowerTab.Details] = new("Cpu", true);
        Require(SortItems(LowerTab.Details, tasks, DetailSortValue).Last().Cpu is null, "Unknown CPU appeared first in descending order.");

        Tabs.SelectedItem = ConnectionsTab;
        TcpConnection Socket(string address, int port) => new(20, false, IPAddress.Parse(address), port, IPAddress.Any, 0, TcpState.Listening);
        _connections = [Socket("10.0.0.10", 9), Socket("10.0.0.2", 100), Socket("10.0.0.2", 9)];
        _tabOrders[LowerTab.Connections] = new("Local", false);
        UpdateConnectionRows();
        Require(_connectionRows.Select(r => r.Local).SequenceEqual(new[] { "10.0.0.2:9", "10.0.0.2:100", "10.0.0.10:9" }), "Incorrect numerical endpoint order.");
        ConnectionList.SelectedItem = _connectionRows[0];
        ConnectionList.UpdateLayout();
        var socketRow = (ListViewItem)ConnectionList.ContainerFromIndex(0);
        Require(socketRow.Focus(Microsoft.UI.Xaml.FocusState.Keyboard), "Could not focus the socket.");
        await ClickHeaderAsync(ConnectionsHeaders, "Local");
        Require(((ConnectionRow)ConnectionList.SelectedItem).Local == "10.0.0.2:9" && ConnectionList.SelectedIndex == 2,
            "A sort changed the selected socket.");
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) as ListViewItem;
        Require(focused?.Content == ConnectionList.SelectedItem, "Keyboard focus moved to a different socket.");
        _services = [new("X", "Alpha", ServiceState.Running, 20), new("Y", "Gamma", ServiceState.Running, 3),
            new("Z", "Beta", ServiceState.Stopped, 0)];
        _tabOrders[LowerTab.Services] = new("Pid", false);
        UpdateServiceRows();
        Require(_serviceRows.Select(r => r.Name).SequenceEqual(new[] { "Y", "X", "Z" }), "Service PIDs sorted lexically or included an empty PID first.");
        _tabOrders[LowerTab.Startup] = new("Command", false);
        UpdateStartupRows(tasks, new StartupItem[] { new("A", "z.exe", StartupSource.UserRun, "", true, "A"),
            new("B", "a.exe", StartupSource.UserRun, "", false, "B") });
        Require(_startupRows.Select(r => r.Name).SequenceEqual(new[] { "B", "A" }), "Startup entries ignored command sorting.");
        CheckNetworkGrouping();
        _tabOrders.Clear();
        UpdateTabHeaders();
        Tabs.SelectedItem = ProcessesTab;
    }

    private void CheckNetworkGrouping()
    {
        NetworkNeighbor Neighbor(string ip) => new(ip, new[] { IPAddress.Parse(ip) }, NeighborState.Reachable, false);
        NetworkAdapter Adapter(string name, long speed, params NetworkNeighbor[] devices) => new(name, name, name,
            NetworkInterfaceType.Ethernet, true, speed, name, [], [], [], devices);
        // The loopback fixtures avoid external name lookups.
        _adapters = [Adapter("slow", 100, Neighbor("127.0.0.10"), Neighbor("127.0.0.2")), Adapter("fast", 1000)];
        _tabOrders[LowerTab.Network] = new("Speed", true);
        UpdateNetworkRows();
        Require(_networkRows.Select(r => r.Key.Adapter).SequenceEqual(new[] { "fast", "slow", "slow", "slow" }),
            "Sorting link speed detached a device from its adapter.");
        _tabOrders[LowerTab.Network] = new("Address", false);
        UpdateNetworkRows();
        Require(_networkRows.Where(r => r.Key.Device != null).Select(r => r.Address).SequenceEqual(new[] { "127.0.0.2", "127.0.0.10" }),
            "Device addresses sorted as text.");
    }
}
