using Pulse.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Telemetry;
using Frame = Pulse.Telemetry.Frame;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Pulse;

public sealed partial class MainWindow : Window
{
    private readonly Sampler _sampler = new();
    private readonly UISettings _systemColors = new();
    private readonly DispatcherTimer _timer = new();
    private readonly ObservableCollection<ProcessRow> _visible = [];
    private readonly HashSet<ProcessKey> _collapsed = [];
    private readonly Dictionary<string, TextBlock> _facts = [];
    private Dictionary<int, TaskSample> _byPid = [];
    private Frame? _frame;
    private ProcessKey? _selected;
    private SortField _sort = SortField.Cpu;
    private bool _descending = true, _tree = true, _refreshing, _busy, _closed, _paused, _restart = true;
    private bool _loaded, _dialogOpen;
    private int _generation;
    private string _path = "";
    private ProcessIdentity _identity = new("", null, "");

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public MainWindow()
    {
        InitializeComponent();
        Root.Language = UiText.Language.Name;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double dpi = GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd), DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min((int)(1190 * dpi), area.Width), Math.Min((int)(805 * dpi), area.Height)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(580 * dpi), area.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(550 * dpi), area.Height);
        }
        TaskList.ItemsSource = _visible;
        CreateTabs();
        Inspector.PaneClosed += (_, _) => { if (!Inspector.IsPaneOpen) ClearSelection(); };
        CpuCard.Configure(UiText.Get("Processor"), "\uE950", true, v => Format.Percent(v));
        MemoryCard.Configure(UiText.Get("Memory"), "\uE964", true, v => Format.Percent(v));
        DiskCard.Configure(UiText.Get("Disk"), "\uEDA2", false, v => Format.Bytes(v, true));
        NetworkCard.Configure(UiText.Get("Network"), "\uE968", false, v => Format.Bytes(v, true));
        CpuCard.EnableSelection(UiText.Get("MomentHint"));
        MemoryCard.EnableSelection(UiText.Get("MomentHint"));
        CpuCard.SampleSelected += (_, time) => ShowMoment(CpuCard, time);
        MemoryCard.SampleSelected += (_, time) => ShowMoment(MemoryCard, time);
        UpdateSpanText();
        CreateDeviceViews();
        CreateFacts();
        UpdateHeaders();
        ApplySystemTheme();
        ApplyCaptionTheme();
        _systemColors.ColorValuesChanged += SystemColorsChanged;
        Root.ActualThemeChanged += (_, _) => ApplyCaptionTheme();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _systemColors.ColorValuesChanged -= SystemColorsChanged;
            _timer.Stop();
            if (!_busy) _sampler.Dispose();
        };
    }

    private void SystemColorsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (!_closed) ApplySystemTheme(); });

    private void ApplySystemTheme()
    {
        var foreground = _systemColors.GetColorValue(UIColorType.Foreground);
        // The foreground follows Windows' app mode independently of the accent color.
        Root.RequestedTheme = (5 * foreground.G + 2 * foreground.R + foreground.B) > 8 * 128
            ? ElementTheme.Dark : ElementTheme.Light;
    }

    private void ApplyCaptionTheme()
    {
        AppWindow.TitleBar.PreferredTheme = Root.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        SetInterval();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy || _closed || _paused) return;
        _busy = true;
        int generation = _generation;
        try
        {
            bool restart = _restart;
            _restart = false;
            // Threads are only sampled for the process shown in the inspector.
            int? inspect = Inspector.IsPaneOpen ? _selected?.Pid : null;
            var frame = await Task.Run(() => _sampler.Sample(restart, inspect));
            if (_closed || _paused || generation != _generation) return;
            _frame = frame;
            _byPid = frame.Tasks.ToDictionary(t => t.Info.Key.Pid);
            var live = frame.Tasks.Select(t => t.Info.Key).ToHashSet();
            _collapsed.RemoveWhere(k => !live.Contains(k));
            UpdateCards(frame);
            UpdateDevices(frame);
            RecordMoment(frame);
            RefreshInspectorSources();
            UpdateRows();
            UpdateTabRows();
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            Uptime.Text = UiText.Format("Uptime", uptime.Days, uptime.Hours, uptime.Minutes);
            Status.Text = UiText.Format("LastUpdate", frame.Clock, frame.Tasks.Sum(t => t.Info.Threads));
            if (ErrorBar.Title == UiText.Get("SampleError")) ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            _restart = true;
            if (!_closed) { ShowError(UiText.Get("SampleError"), ex.Message); Status.Text = UiText.Get("RetryStatus"); }
        }
        finally
        {
            _busy = false;
            if (_closed) _sampler.Dispose();
        }
    }

    private void UpdateCards(Frame f)
    {
        var topCpu = f.Tasks.MaxBy(t => t.Cpu);
        var topRam = f.Tasks.MaxBy(t => t.Info.WorkingSet);
        CpuCard.Update(f.Time, f.Clock, f.Cpu, f.Cpu is { } cpu ? Format.Number(cpu, 1) : "—",
            f.Cpu.HasValue ? "%" : "", UiText.Format("CpuCount", Sampler.LogicalProcessors),
            topCpu?.Cpu is > 0 ? UiText.Format("TopCpu", topCpu.Info.Name, topCpu.Info.Key.Pid, Format.Percent(topCpu.Cpu)) : null);
        double? memory = 100.0 * f.UsedRam / f.TotalRam;
        MemoryCard.Update(f.Time, f.Clock, memory, memory is { } ram ? Format.Number(ram, 1) : "—",
            memory.HasValue ? "%" : "", UiText.Format("MemoryUsage", Format.Bytes(f.UsedRam), Format.Bytes(f.TotalRam)),
            topRam is null ? null : UiText.Format("TopMemory", topRam.Info.Name, topRam.Info.Key.Pid, Format.Bytes(topRam.Info.WorkingSet)));
        double? disk = f.DiskRead + f.DiskWrite;
        var (number, unit) = Format.Size(disk, true);
        DiskCard.Update(f.Time, f.Clock, disk, number, unit, UiText.Format("DiskRates", Format.Bytes(f.DiskRead, true), Format.Bytes(f.DiskWrite, true)));
        double? net = f.Received + f.Sent;
        (number, unit) = Format.Size(net, true);
        NetworkCard.Update(f.Time, f.Clock, net, number, unit, $"↓ {Format.Bytes(f.Received, true)}   ·   ↑ {Format.Bytes(f.Sent, true)}");
    }

    private void UpdateRows()
    {
        if (_frame is null) return;
        string search = SearchBox.Text.Trim();
        var order = TaskOrder.Build(_frame.Tasks, search, _sort, _descending, _tree, _collapsed);
        _refreshing = true;
        try
        {
            int i = 0;
            ProcessRow? selected = null;
            foreach (var item in order)
            {
                var key = item.Task.Info.Key;
                // Rows stay in place and take on whichever process now holds their position.
                // Replacing or moving list items would rebuild their containers and make the list flicker.
                ProcessRow row;
                if (i < _visible.Count) row = _visible[i];
                else _visible.Add(row = new ProcessRow(item.Task));
                row.Update(item, _tree, _collapsed.Contains(key), search.Length != 0);
                if (key == _selected) selected = row;
                i++;
            }
            while (_visible.Count > i) _visible.RemoveAt(_visible.Count - 1);
            if (!ReferenceEquals(TaskList.SelectedItem, selected)) TaskList.SelectedItem = selected;
        }
        finally { _refreshing = false; }
        if (_tab == LowerTab.Processes) CountText.Text = _visible.Count == _frame.Tasks.Count ? $"{_visible.Count}" : $"{_visible.Count} / {_frame.Tasks.Count}";
        EmptyText.Text = search.Length > 0 ? UiText.Get("NoMatches") : UiText.Get("NoProcesses");
        EmptyText.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetails();
    }

    private void SetInterval()
    {
        _timer.Interval = TimeSpan.FromSeconds(IntervalBox.SelectedIndex switch { 0 => 1, 2 => 5, _ => 2 });
        if (_loaded && !_paused) _timer.Start();
    }

    private void IntervalChanged(object sender, SelectionChangedEventArgs e) { if (_loaded) SetInterval(); }

    private async void PauseClicked(object sender, RoutedEventArgs e)
    {
        _paused = PauseButton.IsChecked == true;
        _generation++;
        PauseIcon.Glyph = _paused ? "\uE768" : "\uE769";
        PauseLabel.Text = _paused ? UiText.Get("Resume") : UiText.Get("Pause");
        AutomationProperties.SetName(PauseButton, _paused ? UiText.Get("ResumeUpdates") : UiText.Get("PauseUpdates"));
        StatusIcon.Glyph = _paused ? "\uE769" : "\uE9D9";
        if (_paused)
        {
            _timer.Stop();
            Status.Text = _frame is { } f ? UiText.Format("PausedAt", f.Clock) : UiText.Get("Paused");
            return;
        }
        foreach (var card in new[] { CpuCard, MemoryCard, DiskCard, NetworkCard }) card.ClearHistory();
        ClearDeviceHistory();
        _moments.Clear();
        _restart = true;
        SetInterval();
        await RefreshAsync();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) { UpdateRows(); UpdateTabRows(); }
    private void SearchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
        args.Handled = true;
    }

    private void TreeClicked(object sender, RoutedEventArgs e) { _tree = TreeButton.IsChecked == true; UpdateRows(); }
    private void BranchClicked(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ProcessRow row) return;
        if (!_collapsed.Add(row.Key)) _collapsed.Remove(row.Key);
        UpdateRows();
    }

    private void SortClicked(object sender, RoutedEventArgs e)
    {
        var field = Enum.Parse<SortField>((string)((Button)sender).Tag);
        _descending = field == _sort ? !_descending : field is not (SortField.Pid or SortField.Name);
        _sort = field;
        UpdateHeaders();
        UpdateRows();
    }

    private void UpdateHeaders()
    {
        foreach (var (button, label) in new[] { (NameHeader, UiText.Get("Name")), (PidHeader, "PID"), (CpuHeader, UiText.Get("CPU")),
                     (RamHeader, UiText.Get("Memory")), (IoHeader, UiText.Get("InputOutput")), (ThreadsHeader, UiText.Get("Threads")) })
        {
            bool active = (string)button.Tag == _sort.ToString();
            button.Content = active ? label + (_descending ? " ↓" : " ↑") : label;
            AutomationProperties.SetName(button, active ? UiText.Format(_descending ? "SortDescending" : "SortAscending", label) : label);
        }
    }

    private void TaskSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || TaskList.SelectedItem is not ProcessRow row) return;
        _selected = row.Key;
        DetailName.Text = row.Sample.Info.Name;
        try { _path = ProcessActions.ImagePath(row.Key); }
        catch (Win32Exception ex) { _path = ex.NativeErrorCode == 5 ? UiText.Get("AccessDenied") : ex.Message; }
        catch (InvalidOperationException ex) { _path = ex.Message; }
        _identity = ProcessActions.Describe(row.Key);
        Inspector.IsPaneOpen = true;
        RefreshInspectorSources();
        UpdateDetails();
    }

    private void CreateFacts()
    {
        foreach (var key in new[] { "CPU", "WorkingSet", "PrivateBytes", "ReadRate", "WriteRate", "Threads", "BusyThreads", "Handles", "Session", "User", "Priority", "Started", "Parent", "Services", "Connections", "CommandLine" })
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var label = new TextBlock { Text = UiText.Get(key), FontSize = 13, Opacity = 0.78 };
            var value = new TextBlock { FontSize = 13, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
            DetailFacts.Children.Add(grid);
            _facts[key] = value;
        }
    }

    private void UpdateDetails()
    {
        if (_selected is not { } key || !Inspector.IsPaneOpen) return;
        if (!_byPid.TryGetValue(key.Pid, out var t) || t.Info.Key != key)
        {
            DetailPid.Text = UiText.Format("ProcessExited", key.Pid);
            EndButton.IsEnabled = false;
            return;
        }
        DetailPid.Text = $"PID {key.Pid}";
        DetailPath.Text = _path;
        _facts["CPU"].Text = Format.Percent(t.Cpu);
        _facts["WorkingSet"].Text = Format.Bytes(t.Info.WorkingSet);
        _facts["PrivateBytes"].Text = Format.Bytes(t.Info.PrivateBytes);
        _facts["ReadRate"].Text = Format.Bytes(t.Read, true);
        _facts["WriteRate"].Text = Format.Bytes(t.Write, true);
        _facts["Threads"].Text = Format.Number(t.Info.Threads);
        _facts["Handles"].Text = Format.Number(t.Info.Handles);
        _facts["Session"].Text = t.Info.Session.ToString();
        _facts["Started"].Text = key.Created > 0 ? DateTime.FromFileTime(key.Created).ToString("G") : "—";
        var parent = TaskOrder.ValidParent(t, _byPid);
        _facts["Parent"].Text = parent is not null ? $"{parent.Info.Name} ({t.Info.ParentId})" :
            t.Info.ParentId == 0 ? "—" : UiText.Format("ParentExited", t.Info.ParentId);
        _facts["Services"].Text = HostedServices(key.Pid);
        _facts["BusyThreads"].Text = BusyThreads(key.Pid);
        _facts["User"].Text = _identity.User.Length == 0 ? "—" : _identity.User;
        _facts["Priority"].Text = Format.Priority(_identity.PriorityClass);
        _facts["CommandLine"].Text = _identity.CommandLine.Length == 0 ? "—" : _identity.CommandLine;
        _facts["Connections"].Text = RemoteEndpoints(key.Pid);
        EndButton.IsEnabled = key.Pid > 4 && key.Pid != Environment.ProcessId;
    }

    // The threads of the inspected process that used the most CPU in the last sample.
    private string BusyThreads(int pid)
    {
        if (_frame is not { } frame || frame.ThreadsPid != pid) return "—";
        var busy = frame.Threads.Where(t => t.Cpu is > 0).OrderByDescending(t => t.Cpu).Take(5)
            .Select(t => $"TID {t.Id}   {Format.Percent(t.Cpu)}").ToList();
        return busy.Count == 0 ? "—" : string.Join(Environment.NewLine, busy);
    }

    private void CloseInspector(object sender, RoutedEventArgs e) { Inspector.IsPaneOpen = false; ClearSelection(); }
    private void ClearSelection() { _selected = null; TaskList.SelectedItem = null; }

    private async void AboutClicked(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var assembly = typeof(App).Assembly;
            string author = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company;
            string version = assembly.GetName().Version!.ToString(3);
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.ActualTheme,
                Language = Root.Language,
                Title = UiText.Get("About"),
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = UiText.Format("Version", version) },
                        new TextBlock { Text = UiText.Get("AboutDescription"), TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = UiText.Format("DevelopedBy", author), IsTextSelectionEnabled = true }
                    }
                },
                CloseButtonText = UiText.Get("Close"),
                DefaultButton = ContentDialogButton.Close
            };
            await dialog.ShowAsync();
        }
        finally { _dialogOpen = false; }
    }

    private async void EndClicked(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _selected is not { } key) return;
        _dialogOpen = true;
        string name = DetailName.Text;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Language = Root.Language,
                Title = UiText.Format("ConfirmEnd", name),
                Content = UiText.Format("ConfirmEndBody", key.Pid),
                PrimaryButtonText = UiText.Get("End"), CloseButtonText = UiText.Get("Cancel"), DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closed) return;
            await Task.Run(() => ProcessActions.End(key));
            if (_closed) return;
            DetailPid.Text = UiText.Format("EndRequested", key.Pid);
            EndButton.IsEnabled = false;
            await RefreshAsync();
        }
        catch (Exception ex) { if (!_closed) ShowError(UiText.Get("EndError"), ex.Message); }
        finally { _dialogOpen = false; }
    }

    private void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void TableSizeChanged(object sender, SizeChangedEventArgs e) =>
        TableBody.Width = Math.Max(650, e.NewSize.Width);

    private void RootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 805;
        Workspace.Margin = new Thickness(narrow ? 12 : 24, 8, narrow ? 12 : 24, 12);
        Inspector.DisplayMode = e.NewSize.Width < 1060 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        Cards.RowSpacing = narrow ? 6 : 0;
        // Leave room for the process table in short windows.
        OverviewScroll.MaxHeight = Math.Max(110, e.NewSize.Height - 350);
        DeviceScroll.MaxHeight = Math.Clamp(e.NewSize.Height - 620, 86, 190);
        var cards = new[] { CpuCard, MemoryCard, DiskCard, NetworkCard };
        for (int i = 0; i < cards.Length; i++)
        {
            cards[i].SetCompact(narrow && e.NewSize.Height < 690);
            Grid.SetColumn(cards[i], narrow ? i % 2 * 2 : i);
            Grid.SetColumnSpan(cards[i], narrow ? 2 : 1);
            Grid.SetRow(cards[i], narrow ? i / 2 : 0);
        }
        SearchBox.Width = narrow ? 175 : 214;
        TreeLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
    }
}

