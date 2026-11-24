using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Pulse.Models;
using Windows.System;

namespace Pulse.Controls;

// A compact history graph for one logical processor, physical disk or network adapter.
// Selecting a tile with a click, Enter or Space asks the window to expand it into a larger graph with labelled axes.
public sealed partial class DeviceTile : UserControl
{
    private Func<double, string> _format = v => v.ToString("N0");
    private bool _percent, _expanded;

    public DeviceTile()
    {
        InitializeComponent();
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        Tapped += (_, _) => Selected?.Invoke(this, EventArgs.Empty);
        KeyDown += (_, e) =>
        {
            if (e.Key is not (VirtualKey.Enter or VirtualKey.Space)) return;
            e.Handled = true;
            Selected?.Invoke(this, EventArgs.Empty);
        };
    }

    // Raised when the user clicks the tile or presses Enter or Space on it.
    public event System.EventHandler? Selected;

    internal void Configure(bool percent, Func<double, string> format)
    {
        _percent = percent;
        _format = format;
        Graph.Configure(percent, format);
        UpdateAxis();
    }

    internal void Update(double time, DateTime clock, DeviceReading reading)
    {
        if (Heading.Text != reading.Name)
        {
            Heading.Text = reading.Name;
            ToolTipService.SetToolTip(Heading, reading.Name);
        }
        Reading.Text = reading.Text;
        if (Caption.Text != reading.Caption)
        {
            Caption.Text = reading.Caption;
            ToolTipService.SetToolTip(Caption, reading.Caption.Length == 0 ? null : reading.Caption);
        }
        Caption.Visibility = reading.Caption.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Graph.Add(time, clock, reading.Value);
        UpdateAxis();
        AutomationProperties.SetName(this, $"{reading.Name}, {reading.Text}");
    }

    // A compact percentage graph always ends at 100 %, so its axis stays unlabelled.
    // Other axes show their current maximum, and an expanded tile also shows its time axis.
    private void UpdateAxis()
    {
        bool labelled = _expanded || !_percent;
        AxisLabel.Visibility = labelled ? Visibility.Visible : Visibility.Collapsed;
        if (labelled) AxisLabel.Text = _format(Graph.AxisMaximum);
    }

    internal void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        GraphRow.Height = new GridLength(expanded ? 96 : 30);
        TimeAxis.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        UpdateAxis();
    }

    // Records a sample without a value so the graph shows a break.
    internal void MarkGap(double time, DateTime clock)
    {
        Reading.Text = "—";
        Graph.Add(time, clock, null);
    }

    internal void ClearHistory() => Graph.Clear();

    internal void SetSpan(double seconds, string label)
    {
        Graph.SetSpan(seconds);
        SpanStart.Text = "−" + label;
        UpdateAxis();
    }
}
