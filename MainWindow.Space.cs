using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;
using Windows.Foundation;

namespace Pulse;

public sealed partial class MainWindow
{
    private const int SpaceItemLimit = 60;
    private readonly DispatcherTimer _spaceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<string> _spaceTargets = [];
    private SpaceNode? _spaceRoot, _spaceCurrent;
    private CancellationTokenSource? _spaceCancel;
    private bool _spaceCancelled;
    private SpaceNode? _listedFolder;
    private readonly Dictionary<SpaceNode, SpaceRow> _spaceRows = [];

    private void CreateSpace()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                SpaceDrives.Items.Add(UiText.Format("SpaceDrive", drive.Name, Format.Bytes(drive.AvailableFreeSpace), Format.Bytes(drive.TotalSize)));
                _spaceTargets.Add(drive.RootDirectory.FullName);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        if (_spaceTargets.Count > 0)
        {
            SpaceDrives.SelectedIndex = 0;
        }

        SpaceScan.Content = UiText.Get("SpaceScan");
        SpaceScan.IsEnabled = _spaceTargets.Count > 0;
        SpacePath.Text = UiText.Get("SpacePrompt");
        _spaceTimer.Tick += (_, _) => DrawSpace();

        Closed += (_, _) =>
        {
            _spaceTimer.Stop();
            _spaceCancel?.Cancel();
        };
    }

    private async void SpaceBrowseClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(AppWindow.Id);
            var result = await picker.PickSingleFolderAsync();
            if (result is null || _closed) return;
            SelectSpaceTarget(result.Path);
            await ScanSpaceAsync(result.Path);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            if (!_closed)
            {
                ShowError(UiText.Get("ActionError"), error.Message);
            }
        }
    }

    private void SelectSpaceTarget(string path)
    {
        int index = _spaceTargets.FindIndex(target => string.Equals(path, target, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            index = _spaceTargets.Count;
            _spaceTargets.Add(path);
            SpaceDrives.Items.Add(path);
        }

        SpaceDrives.SelectedIndex = index;
        SpaceScan.IsEnabled = true;
    }

    private async void SpaceScanClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_spaceCancel is { } running)
            {
                await running.CancelAsync();
                return;
            }

            int index = SpaceDrives.SelectedIndex;
            if (index < 0 || index >= _spaceTargets.Count) return;
            await ScanSpaceAsync(_spaceTargets[index]);
        }
        catch (Exception error)
        {
            if (!_closed)
            {
                ShowError(UiText.Get("ActionError"), error.Message);
            }
        }
    }

    private async Task ScanSpaceAsync(string path)
    {
        if (_spaceCancel is not null || _closed) return;
        var root = new SpaceNode(path, null, false);
        var cancel = new CancellationTokenSource();
        _spaceRoot = _spaceCurrent = root;
        _spaceCancel = cancel;
        _spaceCancelled = false;

        try
        {
            SpaceBrowse.IsEnabled = SpaceDrives.IsEnabled = false;
            SpaceScan.Content = UiText.Get("Cancel");
            _spaceTimer.Start();
            DrawSpace();
            await Task.Run(() => SpaceScanner.Scan(root, cancel.Token), cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            _spaceCancelled = true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            _spaceCancelled = true;

            if (!_closed)
            {
                ShowError(UiText.Get("ActionError"), error.Message);
            }
        }
        finally
        {
            cancel.Dispose();
            _spaceCancel = null;

            if (!_closed)
            {
                _spaceTimer.Stop();
                SpaceBrowse.IsEnabled = SpaceDrives.IsEnabled = true;
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

    private void CleanupClicked(object sender, RoutedEventArgs e) => _ = RunActionAsync(() => Process.Start(new ProcessStartInfo("ms-settings:storagerecommendations") { UseShellExecute = true })?.Dispose());

    private void SpaceOpenClicked(object sender, RoutedEventArgs e)
    {
        if (_spaceCurrent is not { } folder) return;

        _ = RunActionAsync(() => Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true, ArgumentList = { folder.Path }
        })?.Dispose());
    }

    private void SpaceMapSizeChanged(object sender, SizeChangedEventArgs e) => DrawSpace();

    private void OpenSpaceFolder(SpaceNode node)
    {
        if (node.IsFile) return;
        _spaceCurrent = node;
        DrawSpace();
    }

    private void SpaceItemMenu(SpaceRow row, Point? position)
    {
        if (row.Node.Grouped > 0) return;
        var menu = new MenuFlyout();
        menu.Items.Add(MenuEntry("MenuShowFile", true, () => _ = RunActionAsync(() => FileExplorer.Select(row.Node.Path))));
        ShowMenu(menu, SpaceItems, position);
    }

    private void SpaceRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SpaceRow row) return;
        SpaceItems.SelectedItem = row;
        SpaceItemMenu(row, e.GetPosition(SpaceItems));
        e.Handled = true;
    }

    private void SpaceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Application || SpaceItems.SelectedItem is not SpaceRow row) return;
        SpaceItemMenu(row, null);
        e.Handled = true;
    }

    private void SpaceItemClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is SpaceRow row) OpenSpaceFolder(row.Node);
    }

    private void DrawSpace()
    {
        if (_tab != LowerTab.Space) return;
        SpaceMap.Children.Clear();

        if (_spaceCurrent is not { } folder || _spaceRoot is not { } root)
        {
            CountText.Text = "";
            return;
        }

        long size = folder.Size;
        string status = _spaceCancel is not null 
            ? UiText.Get("SpaceScanning")
            : _spaceCancelled 
                ? UiText.Get("SpaceCancelled") 
                : string.Empty;

        string path = folder.Unread ? $"{folder.Path} ({UiText.Get("SpaceUnread")})" : folder.Path;
        SpacePath.Text = $"{path}   ·   {Format.Bytes(size)}   ·   {UiText.Get("Allocated")} {Format.Bytes(folder.Allocated)}{(status.Length == 0 ? "" : "   ·   " + status)}";
        ToolTipService.SetToolTip(SpacePath, SpacePath.Text);
        SpaceUp.IsEnabled = folder.Parent is not null;
        SpaceOpen.IsEnabled = true;
        CountText.Text = Format.Bytes(root.Size);

        List<(SpaceNode Node, long Size)> items =
        [
            .. folder.Children
                .Select(node => (Node: node, node.Size))
                .OrderByDescending(item => item.Size)
                .ThenBy(item => item.Node.Name, StringComparer.CurrentCultureIgnoreCase)
        ];

        IEnumerable<(SpaceNode Node, long Size)> listItems = SortItems(LowerTab.Space, items, (item, column) => column switch
        {
            "Name" => SpaceRow.Label(item.Node), "Size" or "Share" => item.Size, "Allocated" => item.Node.Allocated,
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        });

        UpdateSpaceList(folder, listItems, size);
        double width = SpaceMap.ActualWidth, height = SpaceMap.ActualHeight;
        if (width < 20 || height < 20) return;
        List<(SpaceNode Node, long Size)> positive = [.. items.TakeWhile(item => item.Size > 0)];

        List<(SpaceNode?, double)> weights =
            [
                .. positive
                    .Take(SpaceItemLimit)
                    .Select(item => ((SpaceNode?)item.Node, (double)item.Size))
            ];

        long rest = positive.Skip(SpaceItemLimit).Sum(item => item.Size);

        if (rest > 0)
        {
            weights.Add((null, rest));
            weights.Sort((left, right) => right.Item2.CompareTo(left.Item2));
        }

        List<(SpaceNode? Item, Rect Box)> boxes = Squarify(weights, new Rect(0, 0, width, height));

        for (int i = 0; i < boxes.Count; i++)
        {
            (SpaceNode? node, Rect box) = boxes[i];
            if (box.Width < 3 || box.Height < 3) continue;
            string name = node is null ? UiText.Format("SpaceOther", positive.Count - SpaceItemLimit) : SpaceRow.Label(node);
            string label = $"{name}{Environment.NewLine}{Format.Bytes(node?.Size ?? rest)}";

            var tile = new Border
            {
                Width = box.Width - 2, Height = box.Height - 2, CornerRadius = new CornerRadius(3),
                Background = (node?.IsFile == false ? DiskCard : MemoryCard).Foreground,
                Opacity = 0.55 - 0.35 * i / Math.Max(1, boxes.Count)
            };

            ToolTipService.SetToolTip(tile, label);
            if (node is { IsFile: false }) tile.Tapped += (_, _) => OpenSpaceFolder(node);
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

    private void UpdateSpaceList(SpaceNode folder, IEnumerable<(SpaceNode Node, long Size)> items, long total)
    {
        if (!ReferenceEquals(folder, _listedFolder))
        {
            _listedFolder = folder;
            _spaceRows.Clear();
        }

        List<SpaceRow> rows = [];

        foreach ((SpaceNode node, long size) in items)
        {
            if (!_spaceRows.TryGetValue(node, out var row)) _spaceRows[node] = row = new SpaceRow(node);
            row.Refresh(size, total);
            rows.Add(row);
        }

        if (SpaceItems.ItemsSource is List<SpaceRow> current && current.SequenceEqual(rows))
        {
            return;
        }

        var selected = SpaceItems.SelectedItem as SpaceRow;
        var container = FocusManager.GetFocusedElement(Root.XamlRoot) as ListViewItem;
        var focusedRow = container?.Content as SpaceRow;
        var state = container?.FocusState ?? FocusState.Unfocused;
        SpaceItems.ItemsSource = rows;

        if (selected is not null && _spaceRows.ContainsKey(selected.Node))
        {
            SpaceItems.SelectedItem = selected;
        }

        if (focusedRow is not null && _spaceRows.ContainsKey(focusedRow.Node))
        {
            SpaceItems.ScrollIntoView(focusedRow);
            SpaceItems.UpdateLayout();
            (SpaceItems.ContainerFromItem(focusedRow) as ListViewItem)?.Focus(state == FocusState.Unfocused ? FocusState.Programmatic : state);
        }
    }

    private static List<(T Item, Rect Box)> Squarify<T>(IReadOnlyList<(T Item, double Weight)> items, Rect area)
    {
        List<(T Item, Rect Box)> boxes = [];
        double total = items.Sum(item => item.Weight);
        if (total <= 0 || area.Width <= 0 || area.Height <= 0) return boxes;
        double scale = area.Width * area.Height / total;
        double x = area.X, y = area.Y, width = area.Width, height = area.Height;
        int start = 0;

        while (start < items.Count)
        {
            double side = Math.Min(width, height);
            if (side <= 0) break;
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

            double thickness = sum / side, offset = 0;

            for (int i = start; i < end; i++)
            {
                double length = items[i].Weight * scale / thickness;

                boxes.Add((items[i].Item, width >= height
                    ? new Rect(x, y + offset, thickness, length)
                    : new Rect(x + offset, y, length, thickness)));

                offset += length;
            }

            if (width >= height)
            {
                x += thickness; 
                width = Math.Max(0, width - thickness);
            }
            else
            {
                y += thickness; 
                height = Math.Max(0, height - thickness);
            }

            start = end;
        }

        return boxes;
    }
}
