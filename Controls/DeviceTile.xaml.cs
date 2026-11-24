using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pulse.Models;

namespace Pulse.Controls;

// A compact history graph for one logical processor, physical disk or network adapter.
public sealed partial class DeviceTile : UserControl
{
    private Func<double, string> _format = v => v.ToString("N0");
    private bool _percent;

    public DeviceTile() => InitializeComponent();

    internal void Configure(bool percent, Func<double, string> format)
    {
        _percent = percent;
        _format = format;
        Graph.Configure(percent, format);
        // A percentage axis always ends at 100 %. Other axes are labelled with their current maximum.
        AxisLabel.Visibility = percent ? Visibility.Collapsed : Visibility.Visible;
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
        if (!_percent) AxisLabel.Text = _format(Graph.AxisMaximum);
    }

    // Records a sample without a value so the graph shows a break.
    internal void MarkGap(double time, DateTime clock)
    {
        Reading.Text = "—";
        Graph.Add(time, clock, null);
    }

    internal void ClearHistory() => Graph.Clear();

    internal void SetSpan(double seconds)
    {
        Graph.SetSpan(seconds);
        if (!_percent) AxisLabel.Text = _format(Graph.AxisMaximum);
    }
}
