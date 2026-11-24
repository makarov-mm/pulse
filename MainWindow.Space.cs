using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;
using Windows.Foundation;

namespace Pulse;

// The Disk space tab: a background scan shown as a treemap and a list that drill down into folders.
public sealed partial class MainWindow
{
    private const int SpaceItemLimit = 60;
    private readonly DispatcherTimer _spaceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<string> _spaceDrives = [];
    private SpaceNode? _spaceRoot, _spaceCurrent;
    private CancellationTokenSource? _spaceCancel;

    private void CreateSpace()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                SpaceDrives.Items.Add(UiText.Format("SpaceDrive", drive.Name, Format.Bytes(drive.AvailableFreeSpace), Format.Bytes(drive.TotalSize)));
                _spaceDrives.Add(drive.RootDirectory.FullName);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (_spaceDrives.Count > 0) SpaceDrives.SelectedIndex = 0;
        SpaceScan.Content = UiText.Get("SpaceScan");
        SpaceScan.IsEnabled = _spaceDrives.Count > 0;
        SpacePath.Text = UiText.Get("SpacePrompt");
        _spaceTimer.Tick += (_, _) => DrawSpace();
        Closed += (_, _) => { _spaceTimer.Stop(); _spaceCancel?.Cancel(); };
    }

    private async void SpaceScanClicked(object sender, RoutedEventArgs e)
    {
        if (_spaceCancel is { } running)
        {
            running.Cancel();
            return;
        }
        if (SpaceDrives.SelectedIndex < 0 || SpaceDrives.SelectedIndex >= _spaceDrives.Count) return;
        var root = new SpaceNode(_spaceDrives[SpaceDrives.SelectedIndex], null, false);
        var cancel = new CancellationTokenSource();
        _spaceRoot = _spaceCurrent = root;
        _spaceCancel = cancel;
        SpaceScan.Content = UiText.Get("Cancel");
        _spaceTimer.Start();
        DrawSpace();
        try { await Task.Run(() => SpaceScanner.Scan(root, cancel.Token)); }
        catch (OperationCanceledException) { }
        finally
        {
            cancel.Dispose();
            _spaceCancel = null;
            if (!_closed)
            {
                _spaceTimer.Stop();
                SpaceScan.Content = UiText.Get("SpaceScan");
                DrawSpace();
            }
        }
    }

    private void SpaceUpClicked(object sender, RoutedEventArgs e)
    {
        if (_spaceCurrent?.Parent is not { } parent) return;
        _spaceCurrent = parent;
        DrawSpace();
    }

    private void SpaceOpenClicked(object sender, RoutedEventArgs e)
    {
        if (_spaceCurrent is not { } folder) return;
        _ = RunActionAsync(() =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder.Path}\"") { UseShellExecute = true })?.Dispose());
    }

    private void SpaceMapSizeChanged(object sender, SizeChangedEventArgs e) => DrawSpace();

    private void OpenSpaceFolder(SpaceNode node)
    {
        if (node.IsFile) return;
        _spaceCurrent = node;
        DrawSpace();
    }

    private static string SpaceName(SpaceNode node) =>
        node.Grouped > 0 ? UiText.Format("SpaceMoreFiles", node.Grouped) :
        node.Unread ? $"{node.Name} ({UiText.Get("SpaceUnread")})" : node.Name;

    // Redraws the treemap and the list for the current folder. While a scan runs this is called every second.
    private void DrawSpace()
    {
        if (_tab != LowerTab.Space) return;
        SpaceMap.Children.Clear();
        SpaceItems.Children.Clear();
        if (_spaceCurrent is not { } folder || _spaceRoot is not { } root)
        {
            CountText.Text = "";
            return;
        }
        bool scanning = _spaceCancel is not null;
        long size = folder.Size;
        SpacePath.Text = $"{folder.Path}   ·   {Format.Bytes(size)}{(scanning ? "   ·   " + UiText.Get("SpaceScanning") : "")}";
        SpaceUp.IsEnabled = folder.Parent is not null;
        SpaceOpen.IsEnabled = true;
        CountText.Text = Format.Bytes(root.Size);

        var sized = folder.Children.Select(node => (Node: node, Size: node.Size)).Where(item => item.Size > 0)
            .OrderByDescending(item => item.Size).ToList();
        var shown = sized.Take(SpaceItemLimit).ToList();
        long rest = sized.Skip(SpaceItemLimit).Sum(item => item.Size);

        for (int i = 0; i < shown.Count; i++) SpaceItems.Children.Add(SpaceRow(shown[i].Node, shown[i].Size, size));
        if (rest > 0)
            SpaceItems.Children.Add(SpaceRowContent(UiText.Format("SpaceOther", sized.Count - SpaceItemLimit), rest, size));

        double width = SpaceMap.ActualWidth, height = SpaceMap.ActualHeight;
        if (width < 20 || height < 20) return;
        var boxes = Squarify(shown.Select(item => (item.Node, (double)item.Size)).ToList(), new Rect(0, 0, width, height));
        for (int i = 0; i < boxes.Count; i++)
        {
            var (node, box) = boxes[i];
            if (box.Width < 3 || box.Height < 3) continue;
            string label = $"{SpaceName(node)}{Environment.NewLine}{Format.Bytes(node.Size)}";
            // Folders take the disk colour and files the memory colour. Larger items are drawn stronger.
            var tile = new Border
            {
                Width = box.Width - 2, Height = box.Height - 2, CornerRadius = new CornerRadius(3),
                Background = (node.IsFile ? MemoryCard : DiskCard).Foreground,
                Opacity = 0.55 - 0.35 * i / Math.Max(1, boxes.Count)
            };
            ToolTipService.SetToolTip(tile, label);
            if (!node.IsFile) tile.Tapped += (_, _) => OpenSpaceFolder(node);
            Canvas.SetLeft(tile, box.X + 1);
            Canvas.SetTop(tile, box.Y + 1);
            SpaceMap.Children.Add(tile);
            if (box.Width < 70 || box.Height < 40) continue;
            var text = new TextBlock
            {
                Text = label, FontSize = 12, Width = box.Width - 12, MaxHeight = box.Height - 8,
                TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false
            };
            Canvas.SetLeft(text, box.X + 6);
            Canvas.SetTop(text, box.Y + 4);
            SpaceMap.Children.Add(text);
        }
    }

    private UIElement SpaceRow(SpaceNode node, long size, long total)
    {
        var content = SpaceRowContent(SpaceName(node), size, total);
        if (node.IsFile) return content;
        // Folders are buttons so they can be opened with the keyboard as well as the pointer.
        var button = new Button
        {
            Content = content, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        button.Click += (_, _) => OpenSpaceFolder(node);
        return button;
    }

    private static Grid SpaceRowContent(string name, long size, long total)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 5, 8, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        var label = new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        var amount = new TextBlock { Text = Format.Bytes(size), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right };
        var share = new TextBlock
        {
            Text = total > 0 ? Format.Percent(100.0 * size / total) : "—", FontSize = 13, Opacity = 0.78,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        ToolTipService.SetToolTip(label, name);
        Grid.SetColumn(amount, 1);
        Grid.SetColumn(share, 2);
        grid.Children.Add(label);
        grid.Children.Add(amount);
        grid.Children.Add(share);
        return grid;
    }

    // Squarified treemap layout: fills the area row by row, keeping each box as close to a square as it can.
    // Items must be ordered from the largest weight to the smallest and have positive weights.
    private static List<(T Item, Rect Box)> Squarify<T>(IReadOnlyList<(T Item, double Weight)> items, Rect area)
    {
        var boxes = new List<(T Item, Rect Box)>();
        double total = items.Sum(item => item.Weight);
        if (total <= 0 || area.Width <= 0 || area.Height <= 0) return boxes;
        double scale = area.Width * area.Height / total;
        double x = area.X, y = area.Y, width = area.Width, height = area.Height;
        int start = 0;
        while (start < items.Count)
        {
            double side = Math.Min(width, height);
            if (side <= 0) break;
            // Extend the row while doing so improves its worst aspect ratio.
            int end = start;
            double sum = 0, smallest = double.MaxValue, largest = 0, worst = double.MaxValue;
            while (end < items.Count)
            {
                double value = items[end].Weight * scale;
                double next = sum + value, low = Math.Min(smallest, value), high = Math.Max(largest, value);
                double ratio = Math.Max(side * side * high / (next * next), next * next / (side * side * low));
                if (end > start && ratio > worst) break;
                sum = next;
                smallest = low;
                largest = high;
                worst = ratio;
                end++;
            }
            // The row runs along the shorter side and takes a strip off the longer one.
            double thickness = sum / side, offset = 0;
            for (int i = start; i < end; i++)
            {
                double length = items[i].Weight * scale / thickness;
                boxes.Add((items[i].Item, width >= height
                    ? new Rect(x, y + offset, thickness, length)
                    : new Rect(x + offset, y, length, thickness)));
                offset += length;
            }
            if (width >= height) { x += thickness; width = Math.Max(0, width - thickness); }
            else { y += thickness; height = Math.Max(0, height - thickness); }
            start = end;
        }
        return boxes;
    }
}
