using System.Diagnostics;
using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Pulse.Telemetry;

internal sealed record SocketFlow(string Protocol, IPEndPoint Local, IPEndPoint Remote);
internal sealed record ActivityRate(bool Network, int Pid, string Target, double Incoming, double Outgoing, SocketFlow? Socket);

internal sealed class ActivityTrace : IDisposable
{
    // A fixed name lets a new session replace one that a crashed instance left running.
    private const string SessionName = "Makarov-Pulse-Activity-v2";
    private readonly Mutex _owner;
    private readonly TraceEventSession _session;
    private readonly Stopwatch _watch = new();
    private readonly ActivityAccumulator _accumulator;
    private readonly Task _pump;
    private int _lost;

    public ActivityTrace()
    {
        _owner = AcquireOwner(SessionName);
        TraceEventSession? session = null;
        try
        {
            _session = session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.DiskIO |
                KernelTraceEventParser.Keywords.DiskFileIO |
                KernelTraceEventParser.Keywords.NetworkTCPIP |
                KernelTraceEventParser.Keywords.Thread |
                KernelTraceEventParser.Keywords.Process);

            ETWTraceEventSource source = _session.Source;
            _accumulator = new ActivityAccumulator();
            _accumulator.Subscribe(source.Kernel);
            _watch.Restart();
            _pump = Task.Factory.StartNew(source.Process, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            try { session?.Dispose(); }
            finally { _owner.Dispose(); }
            throw;
        }
    }

    // Lost counts the events dropped since the previous call.
    public (List<ActivityRate> Rates, int Lost) Read()
    {
        if (_pump.IsCompleted) throw _pump.Exception?.GetBaseException() ?? new EndOfStreamException();
        double seconds = _watch.Elapsed.TotalSeconds;
        _watch.Restart();
        var (rates, dropped) = _accumulator.Read(seconds);
        int total = _session.Source.EventsLost;
        int lost = dropped + Math.Max(0, total - _lost);
        _lost = total;
        return (rates, lost);
    }

    internal static Mutex AcquireOwner(string name)
    {
        // Lifetime follows the OS handle, even if the owner crashes or disposes on a different thread.
        var owner = new Mutex(false, @"Global\" + name + "-owner", out bool created);
        if (created) return owner;
        owner.Dispose();
        throw new InvalidOperationException(Localization.UiText.Get("TraceInUse"));
    }

    public void Dispose()
    {
        try { _session.Dispose(); }
        finally { _owner.Dispose(); }
    }
}

[System.Reflection.Obfuscation(Exclude = false, ApplyToMembers = true)]
internal sealed class ActivityAccumulator
{
    private const int Limit = 8192;

    // Events arrive per packet, so a flow is keyed by its parts and only described when it is read.
    private readonly record struct Flow(bool Udp, IPAddress Local, int LocalPort, IPAddress Peer, int PeerPort);

    private readonly Lock _sync = new();
    private Dictionary<(int Pid, string Path), (long In, long Out)> _files = [];
    private Dictionary<(int Pid, Flow Flow), (long In, long Out)> _sockets = [];
    private int _dropped;

    internal void Subscribe(KernelTraceEventParser kernel)
    {
        kernel.DiskIORead += e => Disk(e.ProcessID, e.FileName, e.TransferSize, true);
        kernel.DiskIOWrite += e => Disk(e.ProcessID, e.FileName, e.TransferSize, false);
        kernel.TcpIpSend += e => Socket(e.ProcessID, false, e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        kernel.TcpIpRecv += e => Socket(e.ProcessID, false, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
        kernel.TcpIpSendIPV6 += e => Socket(e.ProcessID, false, e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        kernel.TcpIpRecvIPV6 += e => Socket(e.ProcessID, false, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
        kernel.UdpIpSend += e => Socket(e.ProcessID, true, e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        kernel.UdpIpRecv += e => Socket(e.ProcessID, true, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
        kernel.UdpIpSendIPV6 += e => Socket(e.ProcessID, true, e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        kernel.UdpIpRecvIPV6 += e => Socket(e.ProcessID, true, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
    }

    private void Disk(int pid, string? path, int size, bool read)
    {
        if (size <= 0) return;
        lock (_sync) Add(_files, (pid, string.IsNullOrEmpty(path) ? "—" : path), size, read);
    }

    internal void Socket(int pid, bool udp, IPAddress source, int sourcePort, IPAddress destination, int destinationPort,
        int size, bool receive)
    {
        if (size <= 0) return;
        // TCP reports the local endpoint first in both directions. UDP receive reports it second.
        var flow = udp && receive
            ? new Flow(udp, destination, destinationPort, source, sourcePort)
            : new Flow(udp, source, sourcePort, destination, destinationPort);
        lock (_sync) Add(_sockets, (pid, flow), size, receive);
    }

    private void Add<TKey>(Dictionary<TKey, (long In, long Out)> bytes, TKey key, int size, bool incoming) where TKey : notnull
    {
        if (!bytes.TryGetValue(key, out var old) && _files.Count + _sockets.Count >= Limit)
        {
            _dropped++;
            return;
        }

        bytes[key] = incoming
            ? (old.In + size, old.Out)
            : (old.In, old.Out + size);
    }

    // Lost counts the targets left out since the previous call because the table was full.
    public (List<ActivityRate> Rates, int Lost) Read(double seconds)
    {
        Dictionary<(int Pid, string Path), (long In, long Out)> files;
        Dictionary<(int Pid, Flow Flow), (long In, long Out)> sockets;
        int dropped;
        lock (_sync)
        {
            files = _files;
            sockets = _sockets;
            _files = [];
            _sockets = [];
            dropped = _dropped;
            _dropped = 0;
        }

        seconds = Math.Max(0.001, seconds);
        var rows = new List<ActivityRate>(files.Count + sockets.Count);

        foreach (var (key, bytes) in files)
        {
            rows.Add(new ActivityRate(false, key.Pid, key.Path, bytes.In / seconds, bytes.Out / seconds, null));
        }

        foreach (var (key, bytes) in sockets)
        {
            string protocol = key.Flow.Udp ? "UDP" : "TCP";
            var flow = new SocketFlow(protocol, new IPEndPoint(key.Flow.Local, key.Flow.LocalPort), new IPEndPoint(key.Flow.Peer, key.Flow.PeerPort));
            rows.Add(new ActivityRate(true, key.Pid, $"{protocol}  {flow.Local} ↔ {flow.Remote}", bytes.In / seconds, bytes.Out / seconds, flow));
        }

        rows.Sort((a, b) => (b.Incoming + b.Outgoing).CompareTo(a.Incoming + a.Outgoing));
        return (rows, dropped);
    }
}
