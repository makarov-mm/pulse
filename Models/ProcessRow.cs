using Pulse.Localization;
using Microsoft.UI.Xaml;
using Pulse.Telemetry;
using System.ComponentModel;

namespace Pulse.Models;

public sealed class ProcessRow : INotifyPropertyChanged
{
    internal TaskSample Sample { get; private set; }
    internal ProcessKey Key => Sample.Info.Key;
    internal ProcessRow(TaskSample sample) => Sample = sample;
    public string Name => Sample.Info.Name;
    public string Pid => Key.Pid.ToString();
    public string Threads => Format.Number(Sample.Info.Threads);
    public string Cpu => Format.Percent(Sample.Cpu);
    public string Ram => Format.Bytes(Sample.Info.WorkingSet);
    public string Io => Format.Bytes(Sample.Io, true);
    public double CpuWidth => 58 * Math.Clamp(Sample.Cpu ?? 0, 0, 100) / 100;
    public string Glyph => Sample.Info.Session == 0 ? "\uE713" : "\uECAA";
    public Thickness Indent { get; private set; }
    public Visibility BranchSpace { get; private set; }
    public Visibility ArrowVisibility { get; private set; }
    public string Arrow { get; private set; } = "";
    public string BranchAction { get; private set; } = "";
    public bool CanExpand { get; private set; }
    public string Accessible => UiText.Format("RowDescription", Name, Pid, Cpu, Ram, Io);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(Branch branch, bool tree, bool collapsed, bool filtering)
    {
        Sample = branch.Task;
        Indent = new(Math.Min(branch.Depth, 6) * 12, 0, 0, 0);
        BranchSpace = tree ? Visibility.Visible : Visibility.Collapsed;
        ArrowVisibility = branch.HasChildren ? Visibility.Visible : Visibility.Collapsed;
        Arrow = collapsed && !filtering ? "\uE76C" : "\uE70D";
        BranchAction = collapsed ? UiText.Get("ExpandBranch") : UiText.Get("CollapseBranch");
        CanExpand = !filtering;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

