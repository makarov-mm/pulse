using System.Diagnostics;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Pulse.Telemetry;

internal sealed record SocketFlow(string Protocol, IPEndPoint Local, IPEndPoint Remote);
internal sealed record ActivityRate(bool Network, int Pid, string Target, double Incoming, double Outgoing, SocketFlow? Socket);

internal sealed class ActivityTrace : IDisposable
{
    private readonly TraceEventSession _session;
    private readonly Stopwatch _watch = new();
    private readonly ActivityAccumulator _accumulator;
    private readonly Task _pump;

    public ActivityTrace()
    {
        _session = new TraceEventSession("Pulse-Activity-" + Guid.NewGuid().ToString("N")) { StopOnDispose = true };
        try
        {
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskFileIO |
                KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.Process);
            var source = _session.Source;
            _accumulator = new(source.Kernel);
            _watch.Restart();
            _pump = Task.Factory.StartNew(() => source.Process(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { _session.Dispose(); throw; }
    }

    public (List<ActivityRate> Rates, int Lost) Read()
    {
        if (_pump.IsCompleted) throw _pump.Exception?.GetBaseException() ?? new EndOfStreamException();
        double seconds = _watch.Elapsed.TotalSeconds;
        _watch.Restart();
        var (rates, dropped) = _accumulator.Read(seconds);
        return (rates, dropped + _session.Source.EventsLost);
    }

    public void Dispose() => _session.Dispose();
}

internal sealed class ActivityAccumulator
{
    private readonly object _sync = new();
    private readonly Dictionary<(bool Network, int Pid, string Target, SocketFlow? Socket), (long In, long Out)> _bytes = [];
    private int _dropped;

    internal ActivityAccumulator(KernelTraceEventParser kernel)
    {
        kernel.DiskIORead += e => Add(false, e.ProcessID, e.FileName, e.TransferSize, true);
        kernel.DiskIOWrite += e => Add(false, e.ProcessID, e.FileName, e.TransferSize, false);
        kernel.TcpIpSend += e => Socket(e.ProcessID, "TCP", e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        kernel.TcpIpRecv += e => Socket(e.ProcessID, "TCP", e.daddr, e.dport, e.saddr, e.sport, e.size, true);
        kernel.TcpIpSendIPV6 += e => Socket(e.ProcessID, "TCP", e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        kernel.TcpIpRecvIPV6 += e => Socket(e.ProcessID, "TCP", e.daddr, e.dport, e.saddr, e.sport, e.size, true);
        kernel.UdpIpSend += e => Socket(e.ProcessID, "UDP", e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        kernel.UdpIpRecv += e => Socket(e.ProcessID, "UDP", e.daddr, e.dport, e.saddr, e.sport, e.size, true);
        kernel.UdpIpSendIPV6 += e => Socket(e.ProcessID, "UDP", e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        kernel.UdpIpRecvIPV6 += e => Socket(e.ProcessID, "UDP", e.daddr, e.dport, e.saddr, e.sport, e.size, true);
    }

    private void Socket(int pid, string protocol, IPAddress local, int port, IPAddress peer, int remote, int size, bool receive)
    {
        var flow = new SocketFlow(protocol, new(local, port), new(peer, remote));
        Add(true, pid, $"{protocol}  {flow.Local} ↔ {flow.Remote}", size, receive, flow);
    }

    private void Add(bool network, int pid, string target, int size, bool incoming, SocketFlow? flow = null)
    {
        if (size <= 0) return;
        lock (_sync)
        {
            var key = (network, pid, string.IsNullOrEmpty(target) ? "—" : target, flow);
            if (!_bytes.TryGetValue(key, out var old) && _bytes.Count >= 8192) { _dropped++; return; }
            _bytes[key] = incoming ? (old.In + size, old.Out) : (old.In, old.Out + size);
        }
    }

    public (List<ActivityRate> Rates, int Lost) Read(double seconds)
    {
        lock (_sync)
        {
            seconds = Math.Max(0.001, seconds);
            var rows = _bytes.Select(e => new ActivityRate(e.Key.Network, e.Key.Pid, e.Key.Target, e.Value.In / seconds, e.Value.Out / seconds, e.Key.Socket))
                .OrderByDescending(r => r.Incoming + r.Outgoing).ToList();
            _bytes.Clear();
            return (rows, _dropped);
        }
    }

}
