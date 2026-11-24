using Pulse.Localization;
using Microsoft.UI.Xaml;
using Pulse.Telemetry;

namespace Pulse.Models;

public sealed class ProcessRow : ObservableRow
{
    private string _name = "", _pid = "", _threads = "", _cpu = "", _ram = "", _io = "", _glyph = "";
    private string _arrow = "", _branchAction = "", _accessible = "";
    private double _cpuWidth;
    private Thickness _indent;
    private Visibility _branchSpace, _arrowVisibility;
    private bool _canExpand;

    internal TaskSample Sample { get; private set; }
    internal ProcessKey Key => Sample.Info.Key;
    internal ProcessRow(TaskSample sample) => Sample = sample;

    public string Name => _name;
    public string Pid => _pid;
    public string Threads => _threads;
    public string Cpu => _cpu;
    public string Ram => _ram;
    public string Io => _io;
    public double CpuWidth => _cpuWidth;
    public string Glyph => _glyph;
    public Thickness Indent => _indent;
    public Visibility BranchSpace => _branchSpace;
    public Visibility ArrowVisibility => _arrowVisibility;
    public string Arrow => _arrow;
    public string BranchAction => _branchAction;
    public bool CanExpand => _canExpand;
    public string Accessible => _accessible;

    internal void Update(Branch branch, bool tree, bool collapsed, bool filtering)
    {
        Sample = branch.Task;
        var info = Sample.Info;
        // A collapsed branch shows the totals of the process and everything below it.
        var total = branch.Total;
        double? cpu = total is { } sum ? sum.Cpu : Sample.Cpu;
        Set(ref _name, total is { } named ? $"{info.Name} ({named.Processes})" : info.Name, nameof(Name));
        Set(ref _pid, info.Key.Pid.ToString(), nameof(Pid));
        Set(ref _threads, Format.Number(total?.Threads ?? info.Threads), nameof(Threads));
        Set(ref _cpu, Format.Percent(cpu), nameof(Cpu));
        Set(ref _ram, Format.Bytes(total?.WorkingSet ?? info.WorkingSet), nameof(Ram));
        Set(ref _io, Format.Bytes(total is { } summed ? summed.Io : Sample.Io, true), nameof(Io));
        Set(ref _cpuWidth, 58 * Math.Clamp(cpu ?? 0, 0, 100) / 100, nameof(CpuWidth));
        Set(ref _glyph, info.Session == 0 ? "" : "", nameof(Glyph));
        Set(ref _indent, new Thickness(Math.Min(branch.Depth, 6) * 12, 0, 0, 0), nameof(Indent));
        Set(ref _branchSpace, tree ? Visibility.Visible : Visibility.Collapsed, nameof(BranchSpace));
        Set(ref _arrowVisibility, branch.HasChildren ? Visibility.Visible : Visibility.Collapsed, nameof(ArrowVisibility));
        Set(ref _arrow, collapsed && !filtering ? "" : "", nameof(Arrow));
        Set(ref _branchAction, collapsed ? UiText.Get("ExpandBranch") : UiText.Get("CollapseBranch"), nameof(BranchAction));
        Set(ref _canExpand, !filtering, nameof(CanExpand));
        Set(ref _accessible, UiText.Format("RowDescription", _name, _pid, _cpu, _ram, _io), nameof(Accessible));
    }
}
