using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Pulse.Controls;

public sealed partial class MetricCard : UserControl
{
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

    public void SetCompact(bool compact)
    {
        PlotRow.Height = new GridLength(compact ? 17 : 42);
        Caption.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Number.FontSize = compact ? 22 : 29;
    }
}
