namespace Pulse.Models;

// The combined counters of every process in one session.
public sealed class UserRow : ObservableRow
{
    private string _user = "", _session = "", _count = "", _cpu = "", _memory = "", _io = "", _threads = "";

    internal UserRow() { }

    public string User => _user;
    public string Session => _session;
    public string Count => _count;
    public string Cpu => _cpu;
    public string Memory => _memory;
    public string Io => _io;
    public string Threads => _threads;

    internal void Update(string user, uint session, int processes, double? cpu, long workingSet, double? io, int threads)
    {
        Set(ref _user, user, nameof(User));
        Set(ref _session, session.ToString(), nameof(Session));
        Set(ref _count, Format.Number(processes), nameof(Count));
        Set(ref _cpu, Format.Percent(cpu), nameof(Cpu));
        Set(ref _memory, Format.Bytes(workingSet), nameof(Memory));
        Set(ref _io, Format.Bytes(io, true), nameof(Io));
        Set(ref _threads, Format.Number(threads), nameof(Threads));
    }
}
