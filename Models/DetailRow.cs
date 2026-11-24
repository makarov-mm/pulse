using Pulse.Telemetry;

namespace Pulse.Models;

// One process on the Details tab, with every counter in its own column.
public sealed class DetailRow : ObservableRow
{
    private string _name = "", _pid = "", _parent = "", _session = "", _cpu = "", _workingSet = "", _privateBytes = "";
    private string _read = "", _write = "", _threads = "", _handles = "", _started = "";
    private string _priority = "", _user = "", _commandLine = "";
    private string? _commandTip;

    internal DetailRow() { }
    internal int ProcessId { get; private set; }

    public string Name => _name;
    public string Pid => _pid;
    public string Parent => _parent;
    public string Session => _session;
    public string Cpu => _cpu;
    public string WorkingSet => _workingSet;
    public string PrivateBytes => _privateBytes;
    public string Read => _read;
    public string Write => _write;
    public string Threads => _threads;
    public string Handles => _handles;
    public string Started => _started;
    public string Priority => _priority;
    public string User => _user;
    public string CommandLine => _commandLine;
    // Null without a command line, so that no empty tooltip opens.
    public string? CommandTip => _commandTip;

    // identity is null until the process has been described in the background.
    internal void Update(TaskSample task, ProcessIdentity? identity)
    {
        var info = task.Info;
        ProcessId = info.Key.Pid;
        Set(ref _name, info.Name, nameof(Name));
        Set(ref _pid, info.Key.Pid.ToString(), nameof(Pid));
        Set(ref _parent, info.ParentId == 0 ? "" : info.ParentId.ToString(), nameof(Parent));
        Set(ref _session, info.Session.ToString(), nameof(Session));
        Set(ref _cpu, Format.Percent(task.Cpu), nameof(Cpu));
        Set(ref _workingSet, Format.Bytes(info.WorkingSet), nameof(WorkingSet));
        Set(ref _privateBytes, Format.Bytes(info.PrivateBytes), nameof(PrivateBytes));
        Set(ref _read, Format.Bytes(task.Read, true), nameof(Read));
        Set(ref _write, Format.Bytes(task.Write, true), nameof(Write));
        Set(ref _threads, Format.Number(info.Threads), nameof(Threads));
        Set(ref _handles, Format.Number(info.Handles), nameof(Handles));
        Set(ref _started, info.Key.Created > 0 ? DateTime.FromFileTime(info.Key.Created).ToString("G") : "—", nameof(Started));
        Set(ref _priority, identity is null ? "" : Format.Priority(identity.PriorityClass), nameof(Priority));
        Set(ref _user, identity?.User ?? "", nameof(User));
        Set(ref _commandLine, identity?.CommandLine ?? "", nameof(CommandLine));
        Set(ref _commandTip, _commandLine.Length == 0 ? null : _commandLine, nameof(CommandTip));
    }
}
