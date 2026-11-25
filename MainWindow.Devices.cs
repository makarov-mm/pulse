using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Pulse.Controls;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;
using Frame = Pulse.Telemetry.Frame;

namespace Pulse;

public sealed partial class MainWindow
{
    private enum DeviceView { None, Processor, Disk, Network }

    private sealed class DeviceGroup(MetricCard card, string title, string showLabel, bool percent, double minTileWidth)
    {
        public MetricCard Card => card;
        public string Title => title;
        public string ShowLabel => showLabel;
        public bool Percent => percent;
        public double MinTileWidth => minTileWidth;
        public List<(string Key, DeviceTile Tile)> Tiles { get; } = [];
        public string? Expanded { get; set; }
    }

    private readonly Dictionary<DeviceView, DeviceGroup> _devices = [];
    private DeviceView _deviceView;
    private int _deviceColumns;

    private void CreateDeviceViews()
    {
        _devices[DeviceView.Processor] = new DeviceGroup(CpuCard, UiText.Get("LogicalProcessors"), UiText.Get("ShowProcessors"), true, 128);
        _devices[DeviceView.Disk] = new DeviceGroup(DiskCard, UiText.Get("PhysicalDisks"), UiText.Get("ShowDisks"), false, 260);
        _devices[DeviceView.Network] = new DeviceGroup(NetworkCard, UiText.Get("NetworkAdapters"), UiText.Get("ShowAdapters"), false, 260);

        foreach (var (view, group) in _devices)
        {
            group.Card.EnableDetails(group.ShowLabel, UiText.Get("HideDevices"));
            group.Card.DetailsRequested += (_, _) => ToggleDevices(view);
        }
    }

    private void ToggleDevices(DeviceView view)
    {
        _deviceView = _deviceView == view ? DeviceView.None : view;
        foreach ((DeviceView key, DeviceGroup group) in _devices) group.Card.SetExpanded(key == _deviceView);
        DevicePanel.Visibility = _deviceView == DeviceView.None ? Visibility.Collapsed : Visibility.Visible;

        if (_deviceView == DeviceView.None)
        {
            DeviceGrid.Children.Clear();
            return;
        }

        DeviceGroup current = _devices[_deviceView];
        DeviceTitle.Text = current.Title;
        UpdateSpanText();
        LayoutDevices();
        DevicePanel.StartBringIntoView();
    }

    private void CloseDevices(object sender, RoutedEventArgs e)
    {
        if (_deviceView == DeviceView.None) return;
        var card = _devices[_deviceView].Card;
        ToggleDevices(_deviceView);
        card.FocusDetails();
    }

    private void UpdateDevices(Frame f)
    {
        bool groups = f.Cores?.Any(c => c.Group != 0) == true;
        Sync(DeviceView.Processor, f, f.Cores?.Select(c => new DeviceReading($"{c.Group},{c.Number}",
            groups ? UiText.Format("CoreGroupName", c.Group, c.Number) : UiText.Format("CoreName", c.Number),
            c.Usage, Format.Percent(c.Usage), "")));
        Sync(DeviceView.Disk, f, f.Disks?.Select(d => new DeviceReading(d.Instance, DiskName(d),
            d.Read + d.Write, Format.Bytes(d.Read + d.Write, true),
            UiText.Format("DiskDetail", Format.Bytes(d.Read, true), Format.Bytes(d.Write, true),
                Format.Percent(d.Active), Format.Milliseconds(d.Response)))));
        Sync(DeviceView.Network, f, f.Adapters?.Select(a => new DeviceReading(a.Id, a.Name,
            a.Received + a.Sent, Format.Bytes(a.Received + a.Sent, true),
            UiText.Format("AdapterDetail", Format.Bytes(a.Received, true), Format.Bytes(a.Sent, true),
                Format.BitRate(a.LinkSpeed)))));
    }

    private static string DiskName(DiskSample disk) =>
        disk.Index is not { } index ? disk.Instance :
        disk.Volumes.Length == 0 ? UiText.Format("DiskName", index) : UiText.Format("DiskVolumes", index, disk.Volumes);

    private void Sync(DeviceView view, Frame f, IEnumerable<DeviceReading>? source)
    {
        var group = _devices[view];

        if (source is null)
        {
            foreach ((_, DeviceTile tile) in group.Tiles)
            {
                tile.MarkGap(f.Time, f.Clock);
            }

            return;
        }

        Dictionary<string, DeviceTile> known = group.Tiles.ToDictionary(t => t.Key, t => t.Tile);
        List<(string Key, DeviceTile Tile)> next = [];
        HashSet<string> seen = [];

        foreach (var reading in source)
        {
            if (!seen.Add(reading.Key)) continue;

            if (!known.TryGetValue(reading.Key, out var tile))
            {
                tile = new DeviceTile();
                tile.SetBinding(Control.ForegroundProperty, new Binding { Source = group.Card, Path = new PropertyPath("Foreground") });
                if (group.Percent) tile.Configure(true, v => Format.Percent(v));
                else tile.Configure(false, v => Format.Bytes(v, true));
                tile.SetSpan(_span, SpanLabel);
                string key = reading.Key;
                tile.Selected += (_, _) => ExpandDevice(view, key);
            }

            tile.Update(f.Time, f.Clock, reading);
            next.Add((reading.Key, tile));
        }

        bool changed = !next.Select(t => t.Key).SequenceEqual(group.Tiles.Select(t => t.Key));
        group.Tiles.Clear();
        group.Tiles.AddRange(next);

        if (group.Expanded is { } expanded && !seen.Contains(expanded)) group.Expanded = null;
        if (changed && view == _deviceView) LayoutDevices();
    }

    private void ExpandDevice(DeviceView view, string key)
    {
        var group = _devices[view];
        group.Expanded = group.Expanded == key ? null : key;
        if (view != _deviceView) return;
        LayoutDevices();

        foreach ((string tileKey, DeviceTile tile) in group.Tiles)
        {
            if (tileKey == key)
            {
                tile.Focus(FocusState.Programmatic);
            }
        }
    }

    private void ClearDeviceHistory()
    {
        foreach (DeviceGroup group in _devices.Values)
        {
            foreach ((_, DeviceTile tile) in group.Tiles)
            {
                tile.ClearHistory();
            }
        }
    }

    private int DeviceColumns(double width)
    {
        double spacing = DeviceGrid.ColumnSpacing;
        return Math.Max(1, (int)((width + spacing) / (_devices[_deviceView].MinTileWidth + spacing)));
    }

    private void LayoutDevices()
    {
        if (_deviceView == DeviceView.None) return;
        DeviceGroup group = _devices[_deviceView];
        List<(string Key, DeviceTile Tile)> tiles = group.Tiles;
        int columns = _deviceColumns = DeviceColumns(DeviceGrid.ActualWidth);
        bool expanded = tiles.Any(t => t.Key == group.Expanded);
        bool rebuild = tiles.Count != DeviceGrid.Children.Count || !tiles.Select(t => (UIElement)t.Tile).SequenceEqual(DeviceGrid.Children);
        if (rebuild) DeviceGrid.Children.Clear();
        DeviceGrid.ColumnDefinitions.Clear();
        DeviceGrid.RowDefinitions.Clear();

        for (int i = 0; i < columns; i++)
        {
            DeviceGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        int compact = tiles.Count - (expanded ? 1 : 0), firstRow = expanded ? 1 : 0;

        for (int i = 0; i < firstRow + (compact + columns - 1) / columns; i++)
        {
            DeviceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        int cell = 0;

        foreach ((string key, DeviceTile tile) in tiles)
        {
            bool wide = expanded && key == group.Expanded;
            tile.SetExpanded(wide);
            Grid.SetColumn(tile, wide ? 0 : cell % columns);
            Grid.SetRow(tile, wide ? 0 : firstRow + cell / columns);
            Grid.SetColumnSpan(tile, wide ? columns : 1);

            if (!wide) cell++;
            if (rebuild) DeviceGrid.Children.Add(tile);
        }

        DeviceEmpty.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeviceGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_deviceView != DeviceView.None && DeviceColumns(e.NewSize.Width) != _deviceColumns)
        {
            LayoutDevices();
        }
    }
}
