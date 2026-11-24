using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Pulse.Controls;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;
using Frame = Pulse.Telemetry.Frame;

namespace Pulse;

// Device views: one history tile per logical processor, physical disk or network adapter.
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
    }

    private readonly Dictionary<DeviceView, DeviceGroup> _devices = [];
    private DeviceView _deviceView;
    private int _deviceColumns;

    private void CreateDeviceViews()
    {
        _devices[DeviceView.Processor] = new(CpuCard, UiText.Get("LogicalProcessors"), UiText.Get("ShowProcessors"), true, 128);
        _devices[DeviceView.Disk] = new(DiskCard, UiText.Get("PhysicalDisks"), UiText.Get("ShowDisks"), false, 260);
        _devices[DeviceView.Network] = new(NetworkCard, UiText.Get("NetworkAdapters"), UiText.Get("ShowAdapters"), false, 260);
        foreach (var (view, group) in _devices)
        {
            group.Card.EnableDetails(group.ShowLabel, UiText.Get("HideDevices"));
            group.Card.DetailsRequested += (_, _) => ToggleDevices(view);
        }
    }

    private void ToggleDevices(DeviceView view)
    {
        _deviceView = _deviceView == view ? DeviceView.None : view;
        foreach (var (key, group) in _devices) group.Card.SetExpanded(key == _deviceView);
        DevicePanel.Visibility = _deviceView == DeviceView.None ? Visibility.Collapsed : Visibility.Visible;
        if (_deviceView == DeviceView.None)
        {
            DeviceGrid.Children.Clear();
            return;
        }
        var current = _devices[_deviceView];
        DeviceTitle.Text = current.Title;
        DeviceSpan.Text = UiText.Get(current.Percent ? "DeviceSpanPercent" : "DeviceSpanPeak");
        LayoutDevices();
    }

    private void CloseDevices(object sender, RoutedEventArgs e)
    {
        if (_deviceView == DeviceView.None) return;
        var card = _devices[_deviceView].Card;
        ToggleDevices(_deviceView);
        // Return keyboard focus to the button that opened the view.
        card.FocusDetails();
    }

    private void UpdateDevices(Frame f)
    {
        bool groups = f.Cores.Any(c => c.Group != 0);
        Sync(DeviceView.Processor, f, f.Cores.Select(c => new DeviceReading($"{c.Group},{c.Number}",
            groups ? UiText.Format("CoreGroupName", c.Group, c.Number) : UiText.Format("CoreName", c.Number),
            c.Usage, Format.Percent(c.Usage), "")));
        Sync(DeviceView.Disk, f, f.Disks.Select(d => new DeviceReading(d.Instance, DiskName(d),
            d.Read + d.Write, Format.Bytes(d.Read + d.Write, true),
            UiText.Format("DiskDetail", Format.Bytes(d.Read, true), Format.Bytes(d.Write, true),
                Format.Percent(d.Active), Format.Milliseconds(d.Response)))));
        Sync(DeviceView.Network, f, f.Adapters.Select(a => new DeviceReading(a.Id, a.Name,
            a.Received + a.Sent, Format.Bytes(a.Received + a.Sent, true),
            UiText.Format("AdapterDetail", Format.Bytes(a.Received, true), Format.Bytes(a.Sent, true),
                Format.BitRate(a.LinkSpeed)))));
    }

    private static string DiskName(DiskSample disk) =>
        disk.Index is not { } index ? disk.Instance :
        disk.Volumes.Length == 0 ? UiText.Format("DiskName", index) : UiText.Format("DiskVolumes", index, disk.Volumes);

    private void Sync(DeviceView view, Frame f, IEnumerable<DeviceReading> source)
    {
        var group = _devices[view];
        var readings = source.ToList();
        if (readings.Count == 0)
        {
            // A sample without readings is a gap in the data, not proof that every device was removed.
            foreach (var (_, tile) in group.Tiles) tile.MarkGap(f.Time, f.Clock);
            return;
        }

        var known = group.Tiles.ToDictionary(t => t.Key, t => t.Tile);
        var next = new List<(string Key, DeviceTile Tile)>(readings.Count);
        var seen = new HashSet<string>();
        foreach (var reading in readings)
        {
            if (!seen.Add(reading.Key)) continue;
            if (!known.TryGetValue(reading.Key, out var tile))
            {
                tile = new DeviceTile();
                // Follow the card's ink through theme changes.
                tile.SetBinding(Control.ForegroundProperty, new Binding { Source = group.Card, Path = new PropertyPath("Foreground") });
                if (group.Percent) tile.Configure(true, v => Format.Percent(v));
                else tile.Configure(false, v => Format.Bytes(v, true));
            }
            tile.Update(f.Time, f.Clock, reading);
            next.Add((reading.Key, tile));
        }

        bool changed = !next.Select(t => t.Key).SequenceEqual(group.Tiles.Select(t => t.Key));
        group.Tiles.Clear();
        group.Tiles.AddRange(next);
        if (changed && view == _deviceView) LayoutDevices();
    }

    private void ClearDeviceHistory()
    {
        foreach (var group in _devices.Values)
            foreach (var (_, tile) in group.Tiles) tile.ClearHistory();
    }

    private int DeviceColumns(double width)
    {
        double spacing = DeviceGrid.ColumnSpacing;
        return Math.Max(1, (int)((width + spacing) / (_devices[_deviceView].MinTileWidth + spacing)));
    }

    private void LayoutDevices()
    {
        if (_deviceView == DeviceView.None) return;
        var tiles = _devices[_deviceView].Tiles;
        int columns = _deviceColumns = DeviceColumns(DeviceGrid.ActualWidth);
        DeviceGrid.Children.Clear();
        DeviceGrid.ColumnDefinitions.Clear();
        DeviceGrid.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++) DeviceGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (tiles.Count + columns - 1) / columns; i++)
            DeviceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i].Tile;
            Grid.SetColumn(tile, i % columns);
            Grid.SetRow(tile, i / columns);
            DeviceGrid.Children.Add(tile);
        }
        DeviceEmpty.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeviceGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_deviceView != DeviceView.None && DeviceColumns(e.NewSize.Width) != _deviceColumns) LayoutDevices();
    }
}
