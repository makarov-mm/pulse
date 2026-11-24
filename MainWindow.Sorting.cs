using System.Net;
using System.Net.Sockets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;

namespace Pulse;

public sealed partial class MainWindow
{
    private readonly record struct ColumnOrder(string Column, bool Descending);

    private readonly Dictionary<LowerTab, ColumnOrder> _tabOrders = [];
    private readonly Dictionary<Button, (LowerTab Tab, string Label)> _columnLabels = [];

    private void ConfigureTableHeaders()
    {
        foreach (var (tab, grid) in new[] { (LowerTab.Details, DetailsHeaders), (LowerTab.Users, UsersHeaders),
            (LowerTab.Connections, ConnectionsHeaders), (LowerTab.Network, NetworkHeaders), (LowerTab.Startup, StartupHeaders),
            (LowerTab.Services, ServicesHeaders), (LowerTab.Space, SpaceHeaders) })
        {
            foreach (var button in grid.Children.OfType<Button>())
            {
                string label = (string)button.Content;
                _columnLabels.Add(button, (tab, label));
                if (ToolTipService.GetToolTip(button) is null) ToolTipService.SetToolTip(button, label);
            }
        }
        UpdateTabHeaders();
    }

    private ColumnOrder CurrentOrder(LowerTab tab)
    {
        if (_tabOrders.TryGetValue(tab, out var order)) return order;
        return tab switch
        {
            LowerTab.Details => new(_sort switch
            {
                SortField.Ram => "WorkingSet", SortField.Io => "", _ => _sort.ToString()
            }, _descending),
            LowerTab.Users => new("Session", false),
            LowerTab.Connections => new("Host", false),
            LowerTab.Startup or LowerTab.Services => new("Name", false),
            LowerTab.Space => new("Size", true),
            _ => new("", false)
        };
    }

    private void TabSortClicked(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        var (tab, _) = _columnLabels[button];
        string column = (string)button.Tag;
        var previous = CurrentOrder(tab);
        bool descending = previous.Column == column ? !previous.Descending :
            column is "Cpu" or "WorkingSet" or "PrivateBytes" or "Read" or "Write" or "Threads" or
                "Handles" or "Count" or "Memory" or "Io" or "Speed" or "Activity" or "Size" or "Share";
        _tabOrders[tab] = new(column, descending);
        UpdateTabHeaders();
        if (tab == LowerTab.Space) DrawSpace();
        else UpdateTabRows();
    }

    private void UpdateTabHeaders()
    {
        foreach (var (button, heading) in _columnLabels)
        {
            var order = CurrentOrder(heading.Tab);
            bool active = order.Column == (string)button.Tag;
            button.Content = active ? heading.Label + (order.Descending ? " ↓" : " ↑") : heading.Label;
            AutomationProperties.SetName(button, active
                ? UiText.Format(order.Descending ? "SortDescending" : "SortAscending", heading.Label) : heading.Label);
        }
    }

    // The source supplies deterministic tie order. Unknown readings stay last in either direction.
    private IEnumerable<T> SortItems<T>(LowerTab tab, IEnumerable<T> source, Func<T, string, IComparable?> key)
    {
        if (!_tabOrders.TryGetValue(tab, out var order)) return source;
        var comparer = Comparer<IComparable?>.Create((left, right) =>
        {
            if (left is null or "" || right is null or "")
                return (left is null or "").CompareTo(right is null or "");
            if (order.Descending) (left, right) = (right, left);
            return left is string a && right is string b
                ? StringComparer.CurrentCultureIgnoreCase.Compare(a, b) : left.CompareTo(right);
        });
        return source.OrderBy(item => key(item, order.Column), comparer);
    }

    // Socket addresses sort by family, numeric address, scope and numeric port.
    private sealed class AddressOrder(IPAddress address, int port = 0) : IComparable
    {
        private readonly byte[] _bytes = address.GetAddressBytes();
        private readonly long _scope = address.AddressFamily == AddressFamily.InterNetworkV6 ? address.ScopeId : 0;
        private readonly int _port = port;

        public int CompareTo(object? obj)
        {
            var other = (AddressOrder)obj!;
            int result = _bytes.Length.CompareTo(other._bytes.Length);
            if (result == 0) result = _bytes.AsSpan().SequenceCompareTo(other._bytes);
            if (result == 0) result = _scope.CompareTo(other._scope);
            return result == 0 ? _port.CompareTo(other._port) : result;
        }
    }

    private IComparable? DetailSortValue(TaskSample task, string column)
    {
        var info = task.Info;
        var identity = _identities.GetValueOrDefault(info.Key).Identity;
        return column switch
        {
            "Name" => info.Name, "Pid" => info.Key.Pid, "Parent" => info.ParentId == 0 ? null : info.ParentId,
            "Session" => info.Session, "Cpu" => task.Cpu, "WorkingSet" => info.WorkingSet, "PrivateBytes" => info.PrivateBytes,
            "Read" => task.Read, "Write" => task.Write, "Threads" => info.Threads, "Handles" => info.Handles,
            "Started" => info.Key.Created > 0 ? info.Key.Created : null,
            "Priority" => identity?.PriorityClass is null ? null : info.BasePriority,
            "User" => identity?.User, "CommandLine" => identity?.CommandLine,
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };
    }

    private void TableViewportChanged(object sender, SizeChangedEventArgs e)
    {
        var viewport = (Grid)sender;
        if (viewport.Children.FirstOrDefault() is ScrollViewer { Content: Grid body })
        {
            var header = (Grid)((Border)body.Children[0]).Child;
            double minimum = header.ColumnDefinitions.Sum(c => Math.Max(c.MinWidth, c.Width.IsAbsolute ? c.Width.Value : 0))
                + header.Padding.Left + header.Padding.Right + (header.ColumnDefinitions.Count - 1) * header.ColumnSpacing;
            body.Width = Math.Max(e.NewSize.Width, Math.Max(body.MinWidth, minimum));
        }
    }

    private void SpaceLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width < 760;
        var layout = (Grid)sender;
        layout.ColumnDefinitions[0].Width = new GridLength(stacked ? 1 : 3, GridUnitType.Star);
        layout.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        layout.RowDefinitions[1].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        layout.ColumnSpacing = stacked ? 0 : 12;
        Grid.SetRow(SpaceListPanel, stacked ? 1 : 0);
        Grid.SetColumn(SpaceListPanel, stacked ? 0 : 1);
    }
}
