using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;

namespace Pulse;

// The tabs of the lower pane: Processes, Details, Users, Connections, Startup apps, Services and Disk space.
public sealed partial class MainWindow
{
    // In the order of the tab bar.
    private enum LowerTab { Processes, Details, Users, Connections, Startup, Services, Space }

    private readonly ObservableCollection<DetailRow> _detailRows = [];
    private readonly ObservableCollection<UserRow> _userRows = [];
    private readonly ObservableCollection<ConnectionRow> _connectionRows = [];
    private readonly ObservableCollection<StartupRow> _startupRows = [];
    private readonly ObservableCollection<ServiceRow> _serviceRows = [];
    private List<ServiceInfo> _services = [];
    private List<TcpConnection> _connections = [];
    private LowerTab _tab;

    private void CreateTabs()
    {
        DetailList.ItemsSource = _detailRows;
        UserList.ItemsSource = _userRows;
        ConnectionList.ItemsSource = _connectionRows;
        StartupList.ItemsSource = _startupRows;
        ServiceList.ItemsSource = _serviceRows;
        ProcessesTab.Text = UiText.Get("Processes");
        DetailsTab.Text = UiText.Get("DetailsTab");
        UsersTab.Text = UiText.Get("Users");
        ConnectionsTab.Text = UiText.Get("Connections");
        StartupTab.Text = UiText.Get("StartupTab");
        ServicesTab.Text = UiText.Get("Services");
        SpaceTab.Text = UiText.Get("SpaceTab");
        Tabs.SelectedItem = ProcessesTab;
    }

    private void TabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // A selection can be raised while the window is still being built.
        if (ServicesView is null || DetailsView is null || UsersView is null || ConnectionsView is null || StartupView is null || SpaceView is null || Inspector is null || TreeButton is null) return;
        int index = sender.SelectedItem is { } selected ? sender.Items.IndexOf(selected) : 0;
        _tab = index is >= 0 and <= (int)LowerTab.Space ? (LowerTab)index : LowerTab.Processes;
        Inspector.Visibility = Shown(LowerTab.Processes);
        DetailsView.Visibility = Shown(LowerTab.Details);
        UsersView.Visibility = Shown(LowerTab.Users);
        ConnectionsView.Visibility = Shown(LowerTab.Connections);
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
            case LowerTab.Startup: UpdateStartupRows(_frame.Tasks); break;
            case LowerTab.Services: UpdateServiceRows(); break;
            case LowerTab.Space: if (_spaceRoot is { } root) CountText.Text = Format.Bytes(root.Size); else CountText.Text = ""; break;
        }
    }

    // Rows keep their position and take on the item that now holds it, which avoids rebuilding list containers.
    private static void Fill<TRow, TItem>(ObservableCollection<TRow> rows, IReadOnlyList<TItem> items,
        Func<TRow> create, Action<TRow, TItem> update)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i == rows.Count) rows.Add(create());
            update(rows[i], items[i]);
        }
        while (rows.Count > items.Count) rows.RemoveAt(rows.Count - 1);
    }

    private void UpdateDetailRows(IReadOnlyList<TaskSample> tasks)
    {
        // The Details tab is flat and follows the sort order chosen on the Processes tab.
        var matches = TaskOrder.Build(tasks, SearchBox.Text, _sort, _descending, false, _collapsed)
            .Select(branch => branch.Task).ToList();
        Fill(_detailRows, matches, () => new DetailRow(), (row, task) => row.Update(task));
        CountText.Text = matches.Count == tasks.Count ? $"{matches.Count}" : $"{matches.Count} / {tasks.Count}";
    }

    private void UpdateUserRows(IReadOnlyList<TaskSample> tasks)
    {
        string search = SearchBox.Text.Trim();
        var sessions = tasks.GroupBy(t => t.Info.Session)
            .Select(group => (Session: group.Key, User: SessionLabel(group.Key), Tasks: group.ToList()))
            .Where(s => search.Length == 0 || s.User.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(s => s.Session)
            .ToList();
        Fill(_userRows, sessions, () => new UserRow(), (row, s) => row.Update(s.User, s.Session, s.Tasks.Count,
            s.Tasks.Any(t => t.Cpu.HasValue) ? Math.Min(100, s.Tasks.Sum(t => t.Cpu ?? 0)) : (double?)null,
            s.Tasks.Sum(t => t.Info.WorkingSet),
            s.Tasks.Any(t => t.Io.HasValue) ? s.Tasks.Sum(t => t.Io ?? 0) : (double?)null,
            s.Tasks.Sum(t => t.Info.Threads)));
        CountText.Text = $"{sessions.Count}";
    }

    private static string SessionLabel(uint session)
    {
        string user = Sessions.User(session);
        return user.Length != 0 ? user : session == 0 ? UiText.Get("Services") : UiText.Format("SessionName", session);
    }

    private void UpdateStartupRows(IReadOnlyList<TaskSample> tasks)
    {
        string search = SearchBox.Text.Trim();
        var items = StartupItems.Read();
        var running = tasks.Select(t => t.Info.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = items
            .Where(i => search.Length == 0 ||
                i.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                i.Command.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Fill(_startupRows, matches, () => new StartupRow(), (row, item) =>
        {
            string program = StartupItems.Executable(item.Command);
            row.Update(item, running.Contains(program) ? program : "");
        });
        CountText.Text = UiText.Format("StartupCount", items.Count(i => i.Enabled), items.Count);
    }

    private void StartupDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (StartupList.SelectedItem is not StartupRow { Item: { } item }) return;
        try { StartupItems.Open(item); }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException)
        {
            ShowError(UiText.Get("OpenError"), error.Message);
        }
    }

    // Services and connections are only queried while their tab or the process inspector displays them.
    private void RefreshInspectorSources()
    {
        bool inspecting = _tab == LowerTab.Processes && Inspector.IsPaneOpen;
        if (_tab == LowerTab.Services || inspecting)
        {
            try { _services = ServiceSnapshot.Read(); }
            catch (Win32Exception) { _services = []; }
        }
        if (_tab == LowerTab.Connections || inspecting) _connections = TcpConnections.Read();
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
                m.Connection.Local.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                m.Connection.Remote.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Process, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Connection.Pid)
            .ThenBy(m => m.Connection.LocalPort)
            .ToList();
        Fill(_connectionRows, matches, () => new ConnectionRow(), (row, m) => row.Update(m.Connection, m.Process));
        CountText.Text = matches.Count == _connections.Count ? $"{matches.Count}" : $"{matches.Count} / {_connections.Count}";
    }

    private void ConnectionDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ConnectionList.SelectedItem is ConnectionRow { ProcessId: not 0 } row) ShowProcess(row.ProcessId);
    }

    // The peers of a process's open connections, for the inspector.
    private string RemoteEndpoints(int pid)
    {
        const int shown = 6;
        var peers = _connections.Where(c => c.Pid == pid && c.State == TcpState.Established)
            .Select(c => c.Remote).Distinct().ToList();
        if (peers.Count == 0) return "—";
        string text = string.Join(Environment.NewLine, peers.Take(shown));
        return peers.Count > shown ? $"{text}{Environment.NewLine}+{peers.Count - shown}" : text;
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
        Fill(_serviceRows, matches, () => new ServiceRow(),
            (row, service) => row.Update(service, ProcessName(service.Pid)));
        CountText.Text = UiText.Format("ServiceCount", _services.Count(s => s.State == ServiceState.Running), _services.Count);
    }

    private void ServiceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ServiceList.SelectedItem is ServiceRow { ProcessId: not 0 } row) ShowProcess(row.ProcessId);
    }

    private void DetailDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (DetailList.SelectedItem is DetailRow row) ShowProcess(row.ProcessId);
    }

    // Shows a process with its parents on the Processes tab.
    private void ShowProcess(int pid)
    {
        SearchBox.Text = pid.ToString();
        Tabs.SelectedItem = ProcessesTab;
    }

    private void DetailsSizeChanged(object sender, SizeChangedEventArgs e) =>
        DetailsBody.Width = Math.Max(1190, e.NewSize.Width);

    private string HostedServices(int pid)
    {
        string names = string.Join(", ", _services.Where(s => s.Pid == pid).Select(s => s.Name));
        return names.Length == 0 ? "—" : names;
    }
}
