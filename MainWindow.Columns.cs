using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Pulse.Localization;
using Pulse.Models;
using Windows.ApplicationModel.DataTransfer;

namespace Pulse;

public sealed partial class MainWindow
{
    private sealed record ColumnChoice(string Key, double Width, bool Hidden);
    private sealed record TablePreferences(ColumnChoice[] Columns, bool Pin, string? Sort, bool Descending);
    private sealed class Table(LowerTab tab, Grid header, ScrollViewer scroll, Grid body, Button[] buttons)
    {
        public LowerTab Tab = tab;
        public Grid Header = header, Body = body;
        public ScrollViewer Scroll = scroll;
        public double OriginalMinimum = body.MinWidth;
        public Button[] Buttons = buttons;
        public string[] Keys = buttons.Select(b => (string)b.Tag).ToArray();
        public HashSet<Grid> Rows = [];
        public TablePreferences Settings = new([], false, null, false);
    }

    private sealed class GridColumns(Grid grid)
    {
        public (GridLength Width, double Minimum)[] Defaults = grid.ColumnDefinitions.Select(c => (c.Width, c.MinWidth)).ToArray();
        public Dictionary<FrameworkElement, int> Original = grid.Children.OfType<FrameworkElement>().ToDictionary(e => e, e => Grid.GetColumn(e));
        public Border? Shield;
        public List<UIElement> Frozen = [];
    }
    private readonly Dictionary<LowerTab, Table> _tables = [];
    private readonly ConditionalWeakTable<Grid, GridColumns> _gridColumns = new();
    private (Table Table, string Key)? _dragColumn;
    static partial void OverridePreferencesDirectory(ref string directory);

    private static string PreferencesPath(LowerTab tab)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "Tables");
        OverridePreferencesDirectory(ref folder);
        return Path.Combine(folder, tab + ".json");
    }

    private void ConfigureColumns()
    {
        void Register(LowerTab tab, Grid header, ScrollViewer scroller, Grid body)
        {
            var buttons = header.Children.OfType<Button>().OrderBy(Grid.GetColumn).ToArray();
            var t = new Table(tab, header, scroller, body, buttons);
            _tables.Add(tab, t);
            var original = _gridColumns.GetValue(header, g => new(g));
            foreach (var b in buttons)
            {
                string key = (string)b.Tag;
                b.ContextRequested += (_, e) => { ColumnMenu(t, b).ShowAt(b); e.Handled = true; };
                b.CanDrag = true; b.AllowDrop = true;
                b.DragStarting += (_, e) => { _dragColumn = (t, key); e.Data.RequestedOperation = DataPackageOperation.Move; e.Data.SetText(key); };
                b.DragOver += (_, e) => { if (_dragColumn?.Table == t) { e.AcceptedOperation = DataPackageOperation.Move; e.Handled = true; } };
                b.Drop += (_, e) =>
                {
                    if (_dragColumn is { } d && d.Table == t)
                    {
                        Reorder(t, d.Key, key);
                        e.Handled = true;
                    }
                    _dragColumn = null;
                };
                b.DropCompleted += (_, _) => _dragColumn = null;
                var grip = new Thumb { Width = 7, HorizontalAlignment = HorizontalAlignment.Right,
                    IsTabStop = false, Template = (ControlTemplate)Application.Current.Resources["ColumnGripTemplate"] };
                AutomationProperties.SetName(grip, (string)b.Content);
                Grid.SetColumn(grip, Grid.GetColumn(b));
                header.Children.Add(grip); original.Original.Add(grip, Grid.GetColumn(b));
                double width = 0;
                grip.DragStarted += (_, _) => { Materialize(t); width = t.Settings.Columns.Single(c => c.Key == key).Width; };
                grip.DragDelta += (_, e) => { width = Math.Clamp(width + e.HorizontalChange, Minimum(t, key), 1800); Resize(t, key, width, false); };
                grip.DragCompleted += (_, _) => Save(t);
            }
            scroller.SizeChanged += (_, _) => SizeTable(t);
            scroller.ViewChanged += (_, _) => { if (t.Settings.Pin) { Translate(t, header); foreach (var row in t.Rows) Translate(t, row); } };
            try
            {
                var path = PreferencesPath(tab);
                if (File.Exists(path) && new FileInfo(path).Length <= 65536 &&
                    JsonSerializer.Deserialize<TablePreferences>(File.ReadAllText(path)) is { Columns: not null } saved)
                {
                    var valid = saved.Columns.Where(c => c != null && t.Keys.Contains(c.Key) && double.IsFinite(c.Width))
                        .DistinctBy(c => c.Key).Select(c => c with { Width = Math.Clamp(c.Width, Minimum(t, c.Key), 1800) }).ToList();
                    if (valid.Count > 0)
                    {
                        foreach (string key in t.Keys.Except(valid.Select(c => c.Key))) valid.Add(new(key, 140, false));
                        if (valid.All(c => c.Hidden)) valid[0] = valid[0] with { Hidden = false };
                    }
                    t.Settings = saved with { Columns = valid.ToArray(), Pin = saved.Pin && valid.Count != 0 };
                    if (saved.Sort != null && t.Keys.Contains(saved.Sort))
                    {
                        if (tab == LowerTab.Processes) { _sort = Enum.Parse<SortField>(saved.Sort); _descending = saved.Descending; }
                        else _tabOrders[tab] = new(saved.Sort, saved.Descending);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            if (t.Settings.Columns.Length != 0) ApplyTable(t);
        }
        foreach (var (tab, header, view) in new[] { (LowerTab.Processes, ProcessHeaders, TableViewport), (LowerTab.Details, DetailsHeaders, DetailsView),
            (LowerTab.Users, UsersHeaders, UsersView), (LowerTab.Connections, ConnectionsHeaders, ConnectionsView), (LowerTab.Network, NetworkHeaders, NetworkView),
            (LowerTab.Startup, StartupHeaders, StartupView), (LowerTab.Services, ServicesHeaders, ServicesView), (LowerTab.Space, SpaceHeaders, SpaceListPanel) })
        {
            var scroll = (ScrollViewer)view.Children[0];
            Register(tab, header, scroll, (Grid)scroll.Content);
        }
    }

    private static double Minimum(Table t, string key) => t.Tab == LowerTab.Details && key == "Name" ? 260 : 48;
    private static ColumnChoice[] Choices(Table t) => t.Settings.Columns.Length > 0 ? t.Settings.Columns :
        t.Keys.Select((k, i) => new ColumnChoice(k, Math.Clamp(t.Header.ColumnDefinitions[i].ActualWidth, Minimum(t, k), 1800), false)).ToArray();
    private static void Materialize(Table t) => t.Settings = t.Settings with { Columns = Choices(t) };
    private void Resize(Table t, string key, double width, bool persist)
    {
        Materialize(t);
        t.Settings = t.Settings with { Columns = t.Settings.Columns.Select(c => c.Key == key
            ? c with { Width = Math.Clamp(width, Minimum(t, key), 1800) } : c).ToArray() };
        ApplyTable(t); if (persist) Save(t);
    }
    private void Reorder(Table t, string from, string to)
    {
        if (from == to) return;
        Materialize(t);
        var list = t.Settings.Columns.ToList();
        int target = list.FindIndex(c => c.Key == to);
        var item = list.Single(c => c.Key == from);
        list.Remove(item); list.Insert(target, item);
        t.Settings = t.Settings with { Columns = list.ToArray() };
        ApplyTable(t); Save(t);
    }

    private MenuFlyout ColumnMenu(Table t, Button anchor)
    {
        string key = (string)anchor.Tag;
        var columns = Choices(t);
        var menu = new MenuFlyout();
        void Item(string label, Action action)
        {
            var item = new MenuFlyoutItem { Text = UiText.Get(label) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        foreach (var c in columns)
        {
            var b = t.Buttons[Array.IndexOf(t.Keys, c.Key)];
            string label = _columnLabels.TryGetValue(b, out var h) ? h.Label : ((string)b.Content).TrimEnd(' ', '↓', '↑');
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = !c.Hidden,
                IsEnabled = (c.Hidden || columns.Count(x => !x.Hidden) > 1) && !(t.Settings.Pin && c.Key == t.Keys[0]) };
            item.Click += (_, _) =>
            {
                t.Settings = t.Settings with { Columns = columns.Select(x => x.Key == c.Key ? x with { Hidden = !item.IsChecked } : x).ToArray() };
                ApplyTable(t); Save(t);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var pin = new ToggleMenuFlyoutItem { Text = UiText.Get("PinName"), IsChecked = t.Settings.Pin };
        pin.Click += (_, _) =>
        {
            t.Settings = t.Settings with { Pin = pin.IsChecked, Columns = columns.Select(c => pin.IsChecked && c.Key == t.Keys[0] ? c with { Hidden = false } : c).ToArray() };
            ApplyTable(t); Save(t);
        };
        menu.Items.Add(pin);
        int index = Array.FindIndex(columns, c => c.Key == key);
        if (index > 0) Item("MoveLeft", () => Reorder(t, key, columns[index - 1].Key));
        if (index + 1 < t.Keys.Length) Item("MoveRight", () => Reorder(t, key, columns[index + 1].Key));
        Item("Narrower", () => Resize(t, key, columns[index].Width - 24, true));
        Item("Wider", () => Resize(t, key, columns[index].Width + 24, true));
        Item("RestoreLayout", () => { t.Settings = t.Settings with { Columns = [], Pin = false }; ApplyTable(t); Save(t); });
        return menu;
    }

    private void Save(Table t)
    {
        var sort = t.Tab == LowerTab.Processes ? new ColumnOrder(_sort.ToString(), _descending) :
            _tabOrders.TryGetValue(t.Tab, out var s) ? s : new ColumnOrder("", false);
        var data = t.Settings with { Sort = sort.Column, Descending = sort.Descending };
        string path = PreferencesPath(t.Tab), temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowError(UiText.Get("ActionError"), e.Message); }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private void TableRowLoaded(object sender, RoutedEventArgs e)
    {
        var row = (Grid)sender;
        if (!_tables.TryGetValue(Enum.Parse<LowerTab>((string)row.Tag), out var t)) return;
        t.Rows.Add(row);
        if (t.Settings.Columns.Length != 0 || _gridColumns.TryGetValue(row, out _)) ApplyGrid(t, row);
    }
    private void TableRowUnloaded(object sender, RoutedEventArgs e)
    {
        var row = (Grid)sender;
        if (_tables.TryGetValue(Enum.Parse<LowerTab>((string)row.Tag), out var t)) t.Rows.Remove(row);
    }

    private void ApplyTable(Table t)
    {
        ApplyGrid(t, t.Header);
        foreach (var row in t.Rows) ApplyGrid(t, row);
        var columns = t.Settings.Columns;
        double minimum = columns.Length == 0 ? t.OriginalMinimum :
            columns.Where(c => !c.Hidden).Sum(c => c.Width) + t.Header.Padding.Left + t.Header.Padding.Right +
            t.Header.ColumnSpacing * (t.Keys.Length - 1);
        t.Body.MinWidth = minimum;
        SizeTable(t);
    }

    private static void SizeTable(Table t)
    {
        double width = t.Header.ColumnDefinitions.Sum(c => Math.Max(c.MinWidth, c.Width.IsAbsolute ? c.Width.Value : 0))
            + t.Header.Padding.Left + t.Header.Padding.Right + (t.Keys.Length - 1) * t.Header.ColumnSpacing;
        t.Body.Width = Math.Max(t.Scroll.ActualWidth, Math.Max(t.Body.MinWidth, width));
    }

    private void ApplyGrid(Table t, Grid g)
    {
        var original = _gridColumns.GetValue(g, grid => new(grid));
        var choices = t.Settings.Columns;
        if (choices.Length != 0 && t.Settings.Pin) choices = choices.OrderByDescending(c => c.Key == t.Keys[0]).ToArray();
        if (original.Shield != null) { g.Children.Remove(original.Shield); original.Shield = null; }
        original.Frozen.Clear();
        for (int i = 0; i < t.Keys.Length; i++)
        {
            var col = g.ColumnDefinitions[i];
            col.MinWidth = choices.Length == 0 ? original.Defaults[i].Minimum : 0;
            col.Width = choices.Length == 0 ? original.Defaults[i].Width : new GridLength(choices[i].Hidden ? 0 : choices[i].Width);
        }
        foreach (var (element, source) in original.Original)
        {
            int index = choices.Length == 0 ? source : Array.FindIndex(choices, c => c.Key == t.Keys[source]);
            Grid.SetColumn(element, index);
            if (element is FrameworkElement visible && (choices.Length != 0 || element.Visibility == Visibility.Collapsed))
                visible.Visibility = choices.Length != 0 && choices[index].Hidden ? Visibility.Collapsed : Visibility.Visible;
            element.Translation = Vector3.Zero;
            if (t.Settings.Pin && source == 0) original.Frozen.Add(element);
        }
        if (original.Frozen.Count == 0) return;
        var shield = new Border { IsHitTestVisible = false, Style = (Style)Application.Current.Resources["FrozenColumnBackground"] };
        original.Shield = shield;
        g.Children.Add(shield);
        foreach (var item in original.Frozen) { g.Children.Remove(item); g.Children.Add(item); }
        original.Frozen.Insert(0, shield);
        Translate(t, g);
    }
    private void Translate(Table t, Grid grid)
    {
        if (_gridColumns.TryGetValue(grid, out var original))
            foreach (var element in original.Frozen) element.Translation = new((float)t.Scroll.HorizontalOffset, 0, 0);
    }
}
