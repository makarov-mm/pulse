using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;

namespace Pulse;

// The tabs of the lower pane: Processes, Details, Users, Connections, Network, Startup apps, Services and Disk space.
public sealed partial class MainWindow
{
    // In the order of the tab bar.
    private enum LowerTab { Processes, Details, Users, Connections, Network, Startup, Services, Space }

    // A row of the Network tab: an adapter, or one of the devices on its network.
    private readonly record struct NetworkEntry(NetworkAdapter Adapter, NetworkNeighbor? Device);

    private readonly ObservableCollection<DetailRow> _detailRows = [];
    private readonly ObservableCollection<UserRow> _userRows = [];
    private readonly ObservableCollection<ConnectionRow> _connectionRows = [];
    private readonly ObservableCollection<NetworkRow> _networkRows = [];
    private readonly ObservableCollection<StartupRow> _startupRows = [];
    private readonly ObservableCollection<ServiceRow> _serviceRows = [];
    private List<ServiceInfo> _services = [];
    private List<StartupItem> _startupItems = [];
    private string? _serviceReadError;
    private List<TcpConnection> _connections = [];
    private SocketTables _availableTables;
    private List<NetworkAdapter> _adapters = [];
    private readonly Dictionary<ProcessKey, (int BasePriority, ProcessIdentity Identity)> _identities = [];
    private bool _identitiesLoading;
    private bool _networkLoading;
    private LowerTab _tab;

    private void CreateTabs()
    {
        DetailList.ItemsSource = _detailRows;
        UserList.ItemsSource = _userRows;
        ConnectionList.ItemsSource = _connectionRows;
        NetworkList.ItemsSource = _networkRows;
        StartupList.ItemsSource = _startupRows;
        ServiceList.ItemsSource = _serviceRows;
        ProcessesTab.Text = UiText.Get("Processes");
        DetailsTab.Text = UiText.Get("DetailsTab");
        UsersTab.Text = UiText.Get("Users");
        ConnectionsTab.Text = UiText.Get("Connections");
        NetworkTab.Text = UiText.Get("Network");
        StartupTab.Text = UiText.Get("StartupTab");
        ServicesTab.Text = UiText.Get("Services");
        SpaceTab.Text = UiText.Get("SpaceTab");
        Tabs.SelectedItem = ProcessesTab;
    }

    private void TabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // A selection can be raised while the window is still being built.
        if (ServicesView is null || DetailsView is null || UsersView is null || ConnectionsView is null || NetworkView is null || StartupView is null || SpaceView is null || Inspector is null || TreeButton is null) return;
        int index = sender.SelectedItem is { } selected ? sender.Items.IndexOf(selected) : 0;
        _tab = index is >= 0 and <= (int)LowerTab.Space ? (LowerTab)index : LowerTab.Processes;
        Inspector.Visibility = Shown(LowerTab.Processes);
        DetailsView.Visibility = Shown(LowerTab.Details);
        UsersView.Visibility = Shown(LowerTab.Users);
        ConnectionsView.Visibility = Shown(LowerTab.Connections);
        NetworkView.Visibility = Shown(LowerTab.Network);
        StartupView.Visibility = Shown(LowerTab.Startup);
        ServicesView.Visibility = Shown(LowerTab.Services);
        SpaceView.Visibility = Shown(LowerTab.Space);
        // The disk space tab has nothing to search.
        SearchBox.Visibility = _tab == LowerTab.Space ? Visibility.Collapsed : Visibility.Visible;
        TreeButton.Visibility = Shown(LowerTab.Processes);
        SearchBox.PlaceholderText = UiText.Get(_tab switch
        {
            LowerTab.Users => "SearchUsers",
            LowerTab.Connections => "SearchConnections",
            LowerTab.Network => "SearchNetwork",
            LowerTab.Startup => "SearchStartup",
            LowerTab.Services => "SearchServices",
            _ => "Search"
        });
        RefreshInspectorSources();
        UpdateRows();
        UpdateTabRows();
        DrawSpace();
    }

    private Visibility Shown(LowerTab tab) => _tab == tab ? Visibility.Visible : Visibility.Collapsed;

    // Fills the list of the tab on screen. The Processes tab is filled by UpdateRows.
    private void UpdateTabRows()
    {
        if (_frame is null) return;
        switch (_tab)
        {
            case LowerTab.Details: UpdateDetailRows(_frame.Tasks); break;
            case LowerTab.Users: UpdateUserRows(_frame.Tasks); break;
            case LowerTab.Connections: UpdateConnectionRows(); break;
            case LowerTab.Network: UpdateNetworkRows(); break;
            case LowerTab.Startup: UpdateStartupRows(_frame.Tasks, _startupItems); break;
            case LowerTab.Services: UpdateServiceRows(); break;
            case LowerTab.Space: if (_spaceRoot is { } root) CountText.Text = Format.Bytes(root.Size); else CountText.Text = ""; break;
        }
    }

    // Rows keep their position and take on the item that now holds it, which avoids rebuilding list containers.
    private static void Fill<TRow, TItem, TKey>(ListView view, ObservableCollection<TRow> rows, IReadOnlyList<TItem> items,
        Func<TRow, TKey> key, Func<TRow> create, Action<TRow, TItem> update) where TRow : class
    {
        var selected = view.SelectedItem as TRow;
        var selectedKey = selected is null ? default : key(selected);
        var container = view.XamlRoot is null ? null : FocusManager.GetFocusedElement(view.XamlRoot) as ListViewItem;
        var focused = container is not null && view.IndexFromContainer(container) >= 0 ? container.Content as TRow : null;
        var focusedKey = focused is null ? default : key(focused);
        var focusState = container?.FocusState ?? FocusState.Unfocused;
        for (int i = 0; i < items.Count; i++)
        {
            if (i == rows.Count) rows.Add(create());
            update(rows[i], items[i]);
        }
        while (rows.Count > items.Count) rows.RemoveAt(rows.Count - 1);
        TRow? Find(TKey? wanted) => rows.FirstOrDefault(row => EqualityComparer<TKey>.Default.Equals(key(row), wanted));
        if (selected is not null && (!ReferenceEquals(view.SelectedItem, selected) ||
            !EqualityComparer<TKey>.Default.Equals(key(selected), selectedKey)))
            view.SelectedItem = Find(selectedKey);
        if (focused is not null && focusState == FocusState.Keyboard &&
            (!EqualityComparer<TKey>.Default.Equals(key(focused), focusedKey) || !rows.Contains(focused)))
        {
            if (Find(focusedKey) is { } target)
            {
                view.ScrollIntoView(target);
                view.UpdateLayout();
                (view.ContainerFromItem(target) as ListViewItem)?.Focus(FocusState.Keyboard);
            }
        }
    }

    private void UpdateDetailRows(IReadOnlyList<TaskSample> tasks)
    {
        // Follow Processes until Details gets its own order. PID then breaks ties between equal values.
        bool independent = _tabOrders.ContainsKey(LowerTab.Details);
        var matches = TaskOrder.Build(tasks, SearchBox.Text, independent ? SortField.Pid : _sort,
                !independent && _descending, false, _collapsed)
            .Select(branch => branch.Task).ToList();
        matches = SortItems(LowerTab.Details, matches, DetailSortValue).ToList();
        Fill(DetailList, _detailRows, matches, row => row.Key, () => new DetailRow(),
            (row, task) => row.Update(task, _identities.GetValueOrDefault(task.Info.Key).Identity));
        CountText.Text = matches.Count == tasks.Count ? $"{matches.Count}" : $"{matches.Count} / {tasks.Count}";
        _ = LoadIdentitiesAsync(tasks);
    }

    // Describes the processes the Details tab has not seen yet. Opening every process takes too long for the UI thread.
    // A change to the sampled base priority invalidates the cached Windows priority class.
    private async Task LoadIdentitiesAsync(IReadOnlyList<TaskSample> tasks)
    {
        if (_identitiesLoading) return;
        var live = tasks.Select(t => t.Info.Key).ToHashSet();
        foreach (var key in _identities.Keys.Where(k => !live.Contains(k)).ToList()) _identities.Remove(key);
        var missing = tasks.Select(t => (t.Info.Key, t.Info.BasePriority))
            .Where(t => !_identities.TryGetValue(t.Key, out var cached) || cached.BasePriority != t.BasePriority).ToList();
        if (missing.Count == 0) return;
        _identitiesLoading = true;
        try
        {
            var found = await Task.Run(() => missing.Select(t => (t.Key, t.BasePriority, Identity: ProcessActions.Describe(t.Key))).ToList());
            if (_closed) return;
            foreach (var (key, priority, identity) in found) _identities[key] = (priority, identity);
        }
        finally { _identitiesLoading = false; }
        if (_tab == LowerTab.Details && _frame is { } frame) UpdateDetailRows(frame.Tasks);
    }

    private void UpdateUserRows(IReadOnlyList<TaskSample> tasks)
    {
        string search = SearchBox.Text.Trim();
        var sessions = tasks.GroupBy(t => t.Info.Session)
            .Select(group => new
            {
                Session = group.Key, User = SessionLabel(group.Key), Count = group.Count(),
                Cpu = group.Any(t => t.Cpu.HasValue) ? Math.Min(100, group.Sum(t => t.Cpu ?? 0)) : (double?)null,
                Memory = group.Sum(t => t.Info.WorkingSet),
                Io = group.Any(t => t.Io.HasValue) ? group.Sum(t => t.Io ?? 0) : (double?)null,
                Threads = group.Sum(t => t.Info.Threads)
            })
            .Where(s => search.Length == 0 || s.User.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(s => s.Session);
        var sorted = SortItems(LowerTab.Users, sessions, (s, column) => column switch
        {
            "User" => s.User, "Session" => s.Session, "Count" => s.Count, "Cpu" => s.Cpu, "Memory" => s.Memory,
            "Io" => s.Io, "Threads" => s.Threads, _ => throw new ArgumentOutOfRangeException(nameof(column))
        }).ToList();
        Fill(UserList, _userRows, sorted, row => row.Session, () => new UserRow(), (row, s) => row.Update(s.User, s.Session, s.Count,
            s.Cpu, s.Memory, s.Io, s.Threads));
        CountText.Text = $"{sorted.Count}";
    }

    private static string SessionLabel(uint session)
    {
        string user = Sessions.User(session);
        return user.Length != 0 ? user : session == 0 ? UiText.Get("Services") : UiText.Format("SessionName", session);
    }

    private void UpdateStartupRows(IReadOnlyList<TaskSample> tasks, IReadOnlyList<StartupItem> items)
    {
        string search = SearchBox.Text.Trim();
        var running = tasks.Select(t => t.Info.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = items
            .Where(i => search.Length == 0 ||
                i.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                i.Command.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => (Item: item, Program: StartupItems.Executable(item.Command)))
            .Select(entry => (entry.Item, Host: running.Contains(entry.Program) ? entry.Program : ""));
        var sorted = SortItems(LowerTab.Startup, matches, (entry, column) => column switch
        {
            "Name" => entry.Item.Name, "Command" => entry.Item.Command, "Source" => StartupRow.SourceLabel(entry.Item),
            "State" => UiText.Get(entry.Item.Enabled ? "StartupEnabled" : "StartupDisabled"), "Host" => entry.Host,
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        }).ToList();
        Fill(StartupList, _startupRows, sorted, row => (row.Item!.Source, row.Item.Location, row.Item.Entry),
            () => new StartupRow(), (row, entry) => row.Update(entry.Item, entry.Host));
        CountText.Text = UiText.Format("StartupCount", items.Count(i => i.Enabled), items.Count);
    }

    private void StartupDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TappedRow<StartupRow>(e) is { Item: { } item }) _ = EditStartupAsync(item);
    }

    // Services and connections are only queried while their tab or the process inspector displays them.
    private void RefreshInspectorSources()
    {
        if (_tab == LowerTab.Startup) _startupItems = StartupItems.Read();
        bool inspecting = _tab == LowerTab.Processes && Inspector.IsPaneOpen;
        if (_tab == LowerTab.Services || inspecting)
        {
            try
            {
                _services = ServiceSnapshot.Read();
                _serviceReadError = null;
            }
            catch (Win32Exception error)
            {
                _services = [];
                _serviceReadError = UiText.Format("ServicesUnavailable", error.Message);
            }
        }
        if (_tab == LowerTab.Connections || inspecting) _connections = TcpConnections.Read(out _availableTables);
        if (_tab == LowerTab.Network) _ = LoadNetworkAsync();
    }

    // Adapter configuration is slower to read than the other lists, so it is collected off the UI thread.
    private async Task LoadNetworkAsync()
    {
        if (_networkLoading) return;
        _networkLoading = true;
        try
        {
            var adapters = await Task.Run(NetworkDevices.Read);
            if (_closed) return;
            _adapters = adapters;
            if (_tab == LowerTab.Network) UpdateNetworkRows();
        }
        finally { _networkLoading = false; }
    }

    private void UpdateNetworkRows()
    {
        string search = SearchBox.Text.Trim();
        bool Found(string text) => text.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        bool FoundAny(IEnumerable<System.Net.IPAddress> addresses) => addresses.Any(a => Found(a.ToString()));
        var rates = _frame?.Adapters?.ToDictionary(a => a.Id);
        var entries = new List<NetworkEntry>();
        IComparable? AdapterKey(NetworkAdapter adapter, string column)
        {
            var sample = rates?.GetValueOrDefault(adapter.Id);
            return column switch
            {
                "Name" => adapter.Name, "Type" => NetworkRow.TypeName(adapter.Type), "Mac" => adapter.Mac,
                "Address" => adapter.Addresses.FirstOrDefault() is { } ip ? new AddressOrder(ip) : null,
                "State" => UiText.Get(adapter.Connected ? "AdapterConnected" : "AdapterDisconnected"),
                "Speed" => adapter.LinkSpeed, "Activity" => sample?.Received + sample?.Sent,
                _ => throw new ArgumentOutOfRangeException(nameof(column))
            };
        }
        foreach (var adapter in SortItems(LowerTab.Network, _adapters, AdapterKey))
        {
            // An adapter that matches keeps all its devices. Otherwise it is listed for the devices that match.
            bool all = search.Length == 0 || Found(adapter.Name) || Found(adapter.Description) || Found(adapter.Mac) ||
                FoundAny(adapter.Addresses);
            var devices = all ? adapter.Neighbors : adapter.Neighbors
                .Where(d => Found(d.Mac) || FoundAny(d.Addresses) || Found(HostNames.Find(d.Addresses[0]))).ToList();
            if (!all && devices.Count == 0) continue;
            entries.Add(new(adapter, null));
            var sorted = SortItems(LowerTab.Network, devices, (device, column) => column switch
            {
                "Name" => NetworkRow.DeviceName(device, HostNames.Find(device.Addresses[0])),
                "Type" => UiText.Get(device.Gateway ? "NetworkGateway" : "NetworkDevice"),
                "Mac" => device.Mac, "Address" => new AddressOrder(device.Addresses[0]),
                "State" => NetworkRow.StateLabel(device.State), "Speed" or "Activity" => null,
                _ => throw new ArgumentOutOfRangeException(nameof(column))
            });
            entries.AddRange(sorted.Select(device => new NetworkEntry(adapter, device)));
        }
        Fill(NetworkList, _networkRows, entries, row => row.Key, () => new NetworkRow(), (row, entry) =>
        {
            if (entry.Device is { } device) row.Update(entry.Adapter.Id, device, HostNames.Find(device.Addresses[0]));
            else row.Update(entry.Adapter, rates?.GetValueOrDefault(entry.Adapter.Id));
        });
        CountText.Text = UiText.Format("NetworkCount", _adapters.Count(a => a.Connected), _adapters.Count);
    }

    private string ProcessName(int pid) => _byPid.TryGetValue(pid, out var task) ? task.Info.Name : "";

    private void UpdateConnectionRows()
    {
        string search = SearchBox.Text.Trim();
        var matches = _connections
            .Select(c => (Connection: c, Process: ProcessName(c.Pid)))
            .Where(m => search.Length == 0 ||
                m.Process.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                m.Connection.Pid.ToString().Contains(search, StringComparison.Ordinal) ||
                m.Connection.Protocol.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (!m.Connection.Udp && ConnectionRow.StateName(m.Connection.State).Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                m.Connection.Local.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                m.Connection.Remote.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Process, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Connection.Pid)
            .ThenBy(m => m.Connection.LocalPort)
            .ToList();
        matches = SortItems(LowerTab.Connections, matches, (entry, column) => column switch
        {
            "Host" => entry.Process, "Pid" => entry.Connection.Pid, "Protocol" => entry.Connection.Protocol,
            "Local" => new AddressOrder(entry.Connection.LocalAddress, entry.Connection.LocalPort),
            "Remote" => entry.Connection.Remote.Length == 0 ? null : new AddressOrder(entry.Connection.RemoteAddress, entry.Connection.RemotePort),
            "State" => entry.Connection.Udp ? null : ConnectionRow.StateName(entry.Connection.State),
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        }).ToList();
        Fill(ConnectionList, _connectionRows, matches, row => (row.ProcessId, row.Protocol, row.Local, row.Remote),
            () => new ConnectionRow(), (row, m) => row.Update(m.Connection, m.Process));
        string count = matches.Count == _connections.Count ? $"{matches.Count}" : $"{matches.Count} / {_connections.Count}";
        CountText.Text = WithConnectionStatus(count, SocketTables.All, " · ");
    }

    private string WithConnectionStatus(string text, SocketTables requested, string separator)
    {
        var missing = requested & ~_availableTables;
        if (missing == SocketTables.None) return text;
        if (missing == requested) return UiText.Get("ConnectionsUnavailable");
        var names = new List<string>(4);
        if (missing.HasFlag(SocketTables.Tcp4)) names.Add("TCP/IPv4");
        if (missing.HasFlag(SocketTables.Tcp6)) names.Add("TCP/IPv6");
        if (missing.HasFlag(SocketTables.Udp4)) names.Add("UDP/IPv4");
        if (missing.HasFlag(SocketTables.Udp6)) names.Add("UDP/IPv6");
        string status = UiText.Format("ProtocolUnavailable", string.Join(", ", names));
        return text.Length == 0 ? status : text + separator + status;
    }

    private void ConnectionDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TappedRow<ConnectionRow>(e) is { ProcessId: not 0 } row) ShowProcess(row.ProcessId);
    }

    // Separate established conversations from local sockets that can receive traffic.
    private string ProcessEndpoints(int pid, bool bound)
    {
        const int shown = 6;
        var endpoints = _connections.Where(c => c.Pid == pid &&
                (bound ? c.Udp || c.State == TcpState.Listening : !c.Udp && c.State == TcpState.Established))
            .OrderBy(c => c.Udp).ThenBy(c => c.IPv6).ThenBy(c => c.LocalPort)
            .ThenBy(c => c.Local, StringComparer.Ordinal).ThenBy(c => c.Remote, StringComparer.Ordinal)
            .Select(c => bound ? $"{c.Protocol} {c.Local}" : $"{c.Local} → {c.Remote}")
            .Distinct().ToList();
        string text = string.Join(Environment.NewLine, endpoints.Take(shown));
        if (endpoints.Count > shown) text += $"{Environment.NewLine}+{endpoints.Count - shown}";
        text = WithConnectionStatus(text, bound ? SocketTables.All : SocketTables.Tcp, Environment.NewLine);
        return text.Length == 0 ? "—" : text;
    }

    private void UpdateServiceRows()
    {
        string search = SearchBox.Text.Trim();
        var matches = _services
            .Where(s => search.Length == 0 ||
                s.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                s.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                (s.Pid != 0 && s.Pid.ToString().Contains(search, StringComparison.Ordinal)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        matches = SortItems(LowerTab.Services, matches, (service, column) => column switch
        {
            "Name" => service.Name, "Title" => service.DisplayName, "State" => ServiceRow.StateLabel(service.State),
            "Pid" => service.Pid == 0 ? null : service.Pid, "Host" => ProcessName(service.Pid),
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        }).ToList();
        Fill(ServiceList, _serviceRows, matches, row => row.Name, () => new ServiceRow(),
            (row, service) => row.Update(service, ProcessName(service.Pid)));
        CountText.Text = _serviceReadError is null ? UiText.Format("ServiceCount", _services.Count(s => s.State == ServiceState.Running), _services.Count) : UiText.Get("ServicesUnavailableShort");
        ToolTipService.SetToolTip(ServiceList, _serviceReadError);
    }

    private void ServiceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TappedRow<ServiceRow>(e) is { ProcessId: not 0 } row) ShowProcess(row.ProcessId);
    }

    private void DetailDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TappedRow<DetailRow>(e) is { } row) ShowProcess(row.ProcessId);
    }

    // The row under a double-click. A click below the last row is not on any row and must not act on the selected one.
    private static TRow? TappedRow<TRow>(DoubleTappedRoutedEventArgs e) where TRow : class =>
        (e.OriginalSource as FrameworkElement)?.DataContext as TRow;

    // Shows a process with its parents on the Processes tab.
    private void ShowProcess(int pid)
    {
        SearchBox.Text = TaskOrder.PidPrefix + pid;
        Tabs.SelectedItem = ProcessesTab;
    }

    private string HostedServices(int pid)
    {
        if (_serviceReadError is not null) return _serviceReadError;
        string names = string.Join(", ", _services.Where(s => s.Pid == pid).Select(s => s.Name));
        return names.Length == 0 ? "—" : names;
    }
}
