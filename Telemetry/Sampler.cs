using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal sealed record TaskSample(ProcessCounters Info, double? Cpu, double? Read, double? Write)
{
    public double? Io => Read + Write;
}
internal sealed record Frame(double Time, DateTime Clock, double? Cpu, long? TotalRam, long? UsedRam,
    double? DiskRead, double? DiskWrite, double? Received, double? Sent, IReadOnlyList<TaskSample> Tasks,
    IReadOnlyList<CoreSample>? Cores, IReadOnlyList<DiskSample>? Disks, IReadOnlyList<AdapterSample>? Adapters);

internal sealed class Sampler : IDisposable
{
    internal const double MaxSampleInterval = 15;
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong Total, Available, PageTotal, PageAvailable, VirtualTotal, VirtualAvailable, Extended;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public static readonly int LogicalProcessors = Math.Max(1, (int)GetActiveProcessorCount(ushort.MaxValue));
    private readonly ProcessSnapshot _processes = new();
    private readonly PerformanceCounters _performance = new();
    private Dictionary<ProcessKey, ProcessCounters> _previous = [];
    private Dictionary<string, (long Rx, long Tx)> _network = [];
    private double? _lastTime;
    private (long Idle, long Total)? _lastCpu;

    public Frame Sample(bool restart)
    {
        var tasks = _processes.Read();
        double time = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        double seconds = _lastTime is { } previous ? time - previous : 0;
        bool compare = !restart && seconds is > 0 and <= MaxSampleInterval;
        var rows = new List<TaskSample>(tasks.Count);
        foreach (var item in tasks)
        {
            double? cpu = null, read = null, write = null;
            if (compare && _previous.TryGetValue(item.Key, out var old))
            {
                double? ticks = Rate(item.CpuTicks, old.CpuTicks, seconds);
                cpu = ticks is { } t ? Math.Clamp(100 * t / TimeSpan.TicksPerSecond / LogicalProcessors, 0, 100) : null;
                read = Rate(item.ReadBytes, old.ReadBytes, seconds);
                write = Rate(item.WriteBytes, old.WriteBytes, seconds);
            }
            rows.Add(new(item, cpu, read, write));
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        bool haveMemory = GlobalMemoryStatusEx(ref memory) && memory.Total > 0;
        var counters = _performance.Sample();
        double? cpuTotal = counters.Cpu;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long total = kernel + user;
            if (cpuTotal is null && LogicalProcessors <= 64 && _lastCpu is { } old && total > old.Total)
                cpuTotal = 100 * (1 - (double)(idle - old.Idle) / (total - old.Total));
            _lastCpu = (idle, total);
        }
        var (rx, tx, adapters) = NetworkRates(compare, seconds);
        _previous = tasks.ToDictionary(p => p.Key);
        _lastTime = time;
        return new(time, DateTime.Now, compare && cpuTotal is { } c ? Math.Clamp(c, 0, 100) : null,
            haveMemory ? (long)memory.Total : null, haveMemory ? (long)(memory.Total - memory.Available) : null,
            compare ? counters.Read : null, compare ? counters.Write : null, rx, tx, rows,
            compare ? counters.Cores : null, compare ? counters.Disks : null, adapters);
    }

    private static double? Rate(long current, long previous, double seconds) =>
        current >= previous ? (current - previous) / seconds : null;

    private (double? Received, double? Sent, List<AdapterSample>? Adapters) NetworkRates(bool compare, double seconds)
    {
        var current = new Dictionary<string, (long Rx, long Tx)>();
        var adapters = new List<AdapterSample>();
        double rx = 0, tx = 0;
        bool matched = false;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var stats = adapter.GetIPStatistics();
                    long speed = adapter.Speed;
                    current[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
                    double? down = null, up = null;
                    if (compare && _network.TryGetValue(adapter.Id, out var old))
                    {
                        down = Rate(stats.BytesReceived, old.Rx, seconds);
                        up = Rate(stats.BytesSent, old.Tx, seconds);
                    }
                    adapters.Add(new(adapter.Id, adapter.Name, down, up, speed > 0 ? speed : (long?)null));
                    if (down is null || up is null) continue;
                    rx += down.Value;
                    tx += up.Value;
                    matched = true;
                }
                catch (NetworkInformationException)
                {
                    // The adapter was enumerated but its statistics are temporarily unavailable.
                    adapters.Add(new(adapter.Id, adapter.Name, null, null, null));
                }
            }
        }
        catch (NetworkInformationException)
        {
            _network.Clear();
            return (null, null, null);
        }
        adapters.Sort((a, b) =>
        {
            int order = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
            return order != 0 ? order : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });
        _network = current;
        double? received = matched ? rx : null, sent = matched ? tx : null;
        return (received, sent, adapters);
    }

    public void Dispose()
    {
        _processes.Dispose();
        _performance.Dispose();
    }
}

