using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pulse.Controls;
using Pulse.Localization;
using Pulse.Telemetry;

namespace Pulse;

// A separate entry point for the opt-in Windows checks. The release application
// is built with App.xaml.cs and does not contain this test code.
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        UiText.UsePreferredLanguages(new[] { Environment.GetEnvironmentVariable("PULSE_CHECK_LANGUAGE") ?? "en" });
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        ((FrameworkElement)_window.Content).Loaded += CheckWindow;
        _window.Activate();
    }

    private async void CheckWindow(object sender, RoutedEventArgs args)
    {
        ((FrameworkElement)sender).Loaded -= CheckWindow;
        string path = Environment.GetEnvironmentVariable("PULSE_CHECK_REPORT")!;
        try
        {
            await _window!.RunChecksAsync();
            File.WriteAllText(path, $"PASS {UiText.Language.Name}");
        }
        catch (Exception error) { File.WriteAllText(path, error.ToString()); }
        finally { _window!.Close(); }
    }
}

public sealed partial class MainWindow
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal async Task RunChecksAsync()
    {
        for (int i = 0; i < 100 && _frame is null; i++) await Task.Delay(100);
        var frame = _frame ?? throw new InvalidOperationException("The first process sample did not arrive.");
        PauseButton.IsChecked = true;
        PauseClicked(this, new());
        Require(_paused && frame.Tasks.Any(t => t.Info.Key.Pid == Environment.ProcessId), "Missing live process.");
        Require(SearchBox.PlaceholderText == UiText.Get("Search") && Title == UiText.Get("WindowTitle"), "Wrong UI language.");

        var sample = frame with
        {
            Time = frame.Time + 5,
            Disks = new DiskSample[] { new("0 C:", 0, "C:", 1024, 2048, 25, 0.002) },
            Adapters = new AdapterSample[] { new("fixture", "Test adapter", 256, 512, 1_000_000_000) }
        };
        UpdateDevices(sample);
        var opener = (Button)DiskCard.FindName("Expand");
        ((IInvokeProvider)new ButtonAutomationPeer(opener).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(100);
        Require(_deviceView == DeviceView.Disk && DeviceGrid.Children.Count == 1, "The disk view did not open.");
        var tile = _devices[DeviceView.Disk].Tiles.Single().Tile;
        ExpandDevice(DeviceView.Disk, "0 C:");
        await Task.Delay(100);
        Require(ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), tile), "Expanding a tile lost keyboard focus.");
        SpanBox.SelectedIndex = 1;
        Require(((TextBlock)tile.FindName("SpanStart")).Text.Contains(SpanLabel) && _span == 900, "Incorrect expanded time axis.");

        UpdateDevices(sample with { Time = sample.Time + 1, Disks = null, Adapters = Array.Empty<AdapterSample>() });
        Require(_devices[DeviceView.Network].Tiles.Count == 0, "The last disconnected adapter is still displayed.");
        Require(ReferenceEquals(tile, _devices[DeviceView.Disk].Tiles.Single().Tile), "A failed query discarded the disk history.");
        Require(((TextBlock)tile.FindName("Reading")).Text == "—" && ((TextBlock)tile.FindName("Caption")).Text.Length == 0,
            "A missing disk sample still shows old counters.");
        UpdateDevices(sample with { Time = sample.Time + 2 });
        Require(ReferenceEquals(tile, _devices[DeviceView.Disk].Tiles.Single().Tile), "Recovery replaced the disk tile.");

        var size = AppWindow.Size;
        double scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new((int)(585 * scale), (int)(555 * scale)));
        await Task.Delay(200);
        Require(TaskList.ActualHeight >= 36, "Opening devices hid every process row in a short window.");
        AppWindow.Resize(size);
        CloseDevices(this, new());
        await Task.Delay(150);

        var chart = (Sparkline)MemoryCard.FindName("Plot");
        var line = (Microsoft.UI.Xaml.Shapes.Path)chart.FindName("Line");
        var clock = DateTime.Now;
        chart.Clear();
        foreach (int second in new[] { 0, 1, 120, 121 })
            chart.Add(second, clock.AddSeconds(second), 30);
        Require(line.Data is PathGeometry geometry && geometry.Figures.Count == 2, "A line crossed a sampling gap.");
        chart.Clear();
        Require(line.Data is null, "Clearing history left a stale graph on screen.");

        _moments.Clear();
        for (int second = 0; second <= 1000; second++) RecordMoment(sample with { Time = second });
        Require(_moments.Count == 901 && _moments[0].Time == 100, "Unexpected 15-minute retention boundary.");
        PauseButton.IsChecked = false;
        PauseClicked(this, new());
        Require(!_paused && _moments.Count <= 1, "Resuming retained the paused history.");
    }
}
