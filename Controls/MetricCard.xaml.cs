using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Pulse.Controls;

public sealed partial class MetricCard : UserControl
{
    private string _showLabel = "", _hideLabel = "";

    public MetricCard() => InitializeComponent();

    public void Configure(string title, string glyph, bool percent, Func<double, string> format)
    {
        Heading.Text = title;
        Symbol.Glyph = glyph;
        Plot.Configure(percent, format);
    }

    public void Update(double time, DateTime clock, double? value, string number, string unit, string caption, string? detail = null)
    {
        Plot.Add(time, clock, value, detail);
        Number.Text = number;
        Unit.Text = unit;
        Caption.Text = caption;
        ToolTipService.SetToolTip(Caption, caption);
    }

    public void ClearHistory() => Plot.Clear();

    public void SetSpan(double seconds) => Plot.SetSpan(seconds);

    // Raised with the sample time when the user taps the graph. EnableSelection turns it on.
    public event EventHandler<double>? SampleSelected;

    public void EnableSelection(string hint)
    {
        Plot.Hint = hint;
        Plot.SampleSelected += (_, time) => SampleSelected?.Invoke(this, time);
    }

    // Raised when the user asks to open or close the device view behind this card.
    public event EventHandler? DetailsRequested;

    public void EnableDetails(string showLabel, string hideLabel)
    {
        _showLabel = showLabel;
        _hideLabel = hideLabel;
        Expand.Visibility = Visibility.Visible;
        SetExpanded(false);
    }

    public void SetExpanded(bool expanded)
    {
        string label = expanded ? _hideLabel : _showLabel;
        ExpandIcon.Glyph = expanded ? "\uE70E" : "\uE70D";
        AutomationProperties.SetName(Expand, label);
        ToolTipService.SetToolTip(Expand, label);
    }

    public void FocusDetails() => Expand.Focus(FocusState.Programmatic);

    private void ExpandClicked(object sender, RoutedEventArgs e) => DetailsRequested?.Invoke(this, EventArgs.Empty);

    public void SetCompact(bool compact)
    {
        PlotRow.Height = new GridLength(compact ? 17 : 42);
        Caption.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Number.FontSize = compact ? 22 : 29;
    }
}
