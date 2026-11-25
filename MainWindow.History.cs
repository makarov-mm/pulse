using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Pulse.Controls;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;
using Frame = Pulse.Telemetry.Frame;

namespace Pulse;

public sealed partial class MainWindow
{
    private const int MomentProcesses = 5;
    private sealed record Moment(double Time, DateTime Clock, IReadOnlyList<TaskSample> TopCpu, IReadOnlyList<TaskSample> TopMemory);
    private readonly List<Moment> _moments = [];
    private double _span = 60;
    private string SpanLabel => UiText.Get(_span > 60 ? "SpanFifteen" : "SpanOne");

    private void RecordMoment(Frame f)
    {
        _moments.RemoveAll(m => f.Time - m.Time > Sparkline.Retention);
        _moments.Add(new Moment(f.Time, f.Clock,
            [.. f.Tasks.Where(t => t.Cpu is > 0).OrderByDescending(t => t.Cpu).Take(MomentProcesses)],
            [.. f.Tasks.OrderByDescending(t => t.Info.WorkingSet).Take(MomentProcesses)]));
    }

    private void SpanChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _span = SpanBox.SelectedIndex == 1 ? Sparkline.Retention : 60;

        foreach (var card in new[] { CpuCard, MemoryCard, DiskCard, NetworkCard })
        {
            card.SetSpan(_span);
        }

        foreach (DeviceGroup group in _devices.Values)
        {
            foreach ((_, DeviceTile tile) in group.Tiles)
            {
                tile.SetSpan(_span, SpanLabel);
            }
        }

        UpdateSpanText();
    }

    private void UpdateSpanText()
    {
        string span = SpanLabel;
        HostText.Text = UiText.Format("HostHistory", Environment.MachineName, span);

        if (_deviceView != DeviceView.None)
        {
            DeviceSpan.Text = UiText.Format(_devices[_deviceView].Percent ? "DeviceSpanPercent" : "DeviceSpanPeak", span);
        }
    }

    private void ShowMoment(FrameworkElement anchor, double time)
    {
        if (_moments.MinBy(m => Math.Abs(m.Time - time)) is not { } moment) return;
        var panel = new StackPanel { Spacing = 6, MinWidth = 300, MaxWidth = 400 };
        panel.Children.Add(new TextBlock { Text = UiText.Format("MomentTitle", moment.Clock), FontSize = 14, FontWeight = FontWeights.SemiBold });
        AddMomentSection(panel, UiText.Get("MomentCpu"), moment.TopCpu, t => Format.Percent(t.Cpu));
        AddMomentSection(panel, UiText.Get("MomentMemory"), moment.TopMemory, t => Format.Bytes(t.Info.WorkingSet));
        new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom }.ShowAt(anchor);
    }

    private void AddMomentSection(StackPanel panel, string title, IReadOnlyList<TaskSample> tasks, Func<TaskSample, string> value)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = 12, Opacity = 0.78, Margin = new Thickness(0, 6, 0, 0) });

        if (tasks.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = UiText.Get("NoData"), FontSize = 13 });
            return;
        }

        foreach (var task in tasks)
        {
            var key = task.Info.Key;
            bool running = _byPid.TryGetValue(key.Pid, out var live) && live.Info.Key == key;
            string name = $"{task.Info.Name} ({key.Pid})";
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = running ? name : UiText.Format("MomentExited", name),
                FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = running ? 1 : 0.78
            };

            var number = new TextBlock { Text = value(task), FontSize = 13 };
            Grid.SetColumn(number, 1);
            grid.Children.Add(label);
            grid.Children.Add(number);
            panel.Children.Add(grid);
        }
    }
}
