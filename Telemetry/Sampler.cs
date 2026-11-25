using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal sealed record TaskSample(ProcessCounters Info, double? Cpu, double? Read, double? Write)
{
    public GpuLoad? Gpu { get; init; }
    public double? Io => Read + Write;
}
internal sealed record ThreadSample(int Id, double? Cpu);

internal sealed record Frame(double Time, double? Cpu, long? TotalRam, long? UsedRam,
    double? DiskRead, double? DiskWrite, double? Received, double? Sent, IReadOnlyList<TaskSample> Tasks,
    IReadOnlyList<CoreSample>? Cores, IReadOnlyList<DiskSample>? Disks, IReadOnlyList<AdapterSample>? Adapters)
{
    public GpuSnapshot? Gpu { get; init; }
    public bool DiskRatesFromVolumes { get; init; }
    // Thread samples belong to this process instance even if Windows reuses the PID.
    public ProcessKey? ThreadOwner { get; init; }
    public IReadOnlyList<ThreadSample> Threads { get; init; } = [];
}

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
    [DllImport("iphlpapi.dll")]
    private static extern uint ConvertInterfaceGuidToLuid(in Guid guid, out ulong luid);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfEntry2(nint row);

    // MIB_IF_ROW2 starts with the interface LUID. InterfaceAndOperStatusFlags follows DirectionType.
    private const int InterfaceRowBytes = 1352, InterfaceFlagsOffset = 1152;
    private const byte HardwareInterface = 0x01, FilterInterface = 0x02;

    public static readonly int LogicalProcessors = Math.Max(1, (int)GetActiveProcessorCount(ushort.MaxValue));
    private readonly ProcessSnapshot _processes = new();
    private readonly PerformanceCounters _performance = new();
    private readonly ProcessorTimes _processorTimes = new();
    private readonly VolumeActivity _volumeActivity = new();
    private Dictionary<ProcessKey, ProcessCounters> _previous = [];
    private Dictionary<string, (long Rx, long Tx)> _network = [];
    private readonly Dictionary<string, byte> _interfaceFlags = [];
    private Dictionary<int, ThreadCounters> _threads = [];
    private ProcessKey? _threadOwner;
    private double? _lastTime;
    private (long Idle, long Total)? _lastCpu;

    // inspectPid selects a process whose threads are sampled as well.
    public Frame Sample(bool restart, int? inspectPid = null)
    {
        var tasks = _processes.Read(inspectPid, out var threads);
        double time = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        double seconds = _lastTime is { } previous ? time - previous : 0;
        bool compare = !restart && seconds is > 0 and <= MaxSampleInterval;
        var counters = _performance.Sample();
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
            rows.Add(new(item, cpu, read, write) { Gpu = compare && _previous.ContainsKey(item.Key) ? counters.Gpu?.Processes.GetValueOrDefault(item.Key.Pid) : null });
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        bool haveMemory = GlobalMemoryStatusEx(ref memory) && memory.Total > 0;
        var processor = _processorTimes.Read(compare, counters.Cores);
        double? cpuTotal = counters.Cpu ?? processor.Total;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long total = kernel + user;
            if (cpuTotal is null && LogicalProcessors <= 64 && _lastCpu is { } old && total > old.Total)
                cpuTotal = 100 * (1 - (double)(idle - old.Idle) / (total - old.Total));
            _lastCpu = (idle, total);
        }
        var (diskRead, diskWrite, disks, fromVolumes) = DiskRates(counters, time, compare);
        var (rx, tx, adapters) = NetworkRates(compare, seconds);
        _previous = tasks.ToDictionary(p => p.Key);
        _lastTime = time;
        var owner = inspectPid.HasValue ? tasks.FirstOrDefault(t => t.Key.Pid == inspectPid)?.Key : null;
        var threadRows = new List<ThreadSample>(threads.Count);
        foreach (var thread in threads)
        {
            double? cpu = null;
            if (compare && owner.HasValue && _threadOwner == owner && _threads.TryGetValue(thread.Id, out var old) &&
                old.Created == thread.Created && Rate(thread.CpuTicks, old.CpuTicks, seconds) is { } ticks)
                cpu = Math.Clamp(100 * ticks / TimeSpan.TicksPerSecond / LogicalProcessors, 0, 100);
            threadRows.Add(new(thread.Id, cpu));
        }
        _threads = threads.ToDictionary(t => t.Id);
        _threadOwner = owner;
        return new Frame(time, compare && cpuTotal is { } c ? Math.Clamp(c, 0, 100) : null,
            haveMemory ? (long)memory.Total : null, haveMemory ? (long)(memory.Total - memory.Available) : null,
            diskRead, diskWrite, rx, tx, rows,
            processor.Cores, disks, adapters)
        {
            DiskRatesFromVolumes = fromVolumes, Gpu = counters.Gpu, ThreadOwner = owner, Threads = threadRows
        };
    }

    private (double? Read, double? Write, IReadOnlyList<DiskSample>? Disks, bool FromVolumes)
        DiskRates(CounterSample counters, double time, bool compare)
    {
        var physical = compare ? counters.Disks : null;
        double? read = counters.Read ?? CompleteSum(physical, d => d.Read);
        double? write = counters.Write ?? CompleteSum(physical, d => d.Write);
        bool physicalRows = physical?.Any(d => d.Read.HasValue || d.Write.HasValue) == true;
        if (compare && read.HasValue && write.HasValue && physicalRows)
        {
            _volumeActivity.Reset();
            return (read, write, physical, false);
        }

        var volumes = _volumeActivity.Sample(time, compare);
        if (!compare) return (null, null, volumes, false);
        bool useVolumeRows = !physicalRows && volumes is { Count: > 0 };
        var rows = useVolumeRows ? volumes : physical;
        if (read.HasValue && write.HasValue) return (read, write, rows, false);

        var measured = volumes?.Where(d => d.Read.HasValue && d.Write.HasValue).ToArray();
        if (measured is { Length: > 0 })
            return (measured.Sum(d => d.Read), measured.Sum(d => d.Write), volumes, true);
        return (read, write, rows, false);
    }

    private static double? CompleteSum(IReadOnlyList<DiskSample>? disks, Func<DiskSample, double?> value)
    {
        if (disks is not { Count: > 0 }) return null;
        double total = 0;
        foreach (var disk in disks)
        {
            if (value(disk) is not { } rate) return null;
            total += rate;
        }
        return total;
    }

    internal static double? Rate(long current, long previous, double seconds) =>
        seconds > 0 && previous >= 0 && current >= previous ? (current - previous) / seconds : null;

    private (double? Received, double? Sent, List<AdapterSample>? Adapters) NetworkRates(bool compare, double seconds)
    {
        var current = new Dictionary<string, (long Rx, long Tx)>();
        var adapters = new List<AdapterSample>();
        var hardware = new List<AdapterSample>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                byte flags = GetInterfaceFlags(adapter.Id);
                if ((flags & FilterInterface) != 0) continue;
                AdapterSample sample;
                try
                {
                    var stats = adapter.GetIPStatistics();
                    current[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
                    double? down = null, up = null;
                    if (compare && _network.TryGetValue(adapter.Id, out var old))
                    {
                        down = Rate(stats.BytesReceived, old.Rx, seconds);
                        up = Rate(stats.BytesSent, old.Tx, seconds);
                    }
                    long? speed = null;
                    try
                    {
                        long bits = adapter.Speed;
                        if (bits > 0) speed = bits;
                    }
                    catch (NetworkInformationException) { /* Link speed is optional. */ }
                    sample = new(adapter.Id, adapter.Name, down, up, speed);
                }
                catch (NetworkInformationException)
                {
                    // The adapter was enumerated but its statistics are temporarily unavailable.
                    sample = new(adapter.Id, adapter.Name, null, null, null);
                }
                adapters.Add(sample);
                if ((flags & HardwareInterface) != 0) hardware.Add(sample);
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
        // Traffic of a virtual switch or VPN adapter also crosses a physical adapter. Counting only
        // hardware interfaces avoids double counting. Virtual-only systems use the available adapters.
        var (rx, tx) = AdapterTotals(hardware.Count > 0 ? hardware : adapters, compare);
        return (rx, tx, adapters);
    }

    internal static (double? Received, double? Sent) AdapterTotals(IReadOnlyList<AdapterSample> adapters, bool compare)
    {
        if (!compare) return (null, null);
        if (adapters.Count == 0) return (0, 0);
        double? rx = null, tx = null;
        foreach (var adapter in adapters)
        {
            if (adapter.Received is { } down) rx = (rx ?? 0) + down;
            if (adapter.Sent is { } up) tx = (tx ?? 0) + up;
        }
        return (rx, tx);
    }

    // Retry failed interface lookups on later samples instead of caching a guessed classification.
    private byte GetInterfaceFlags(string id)
    {
        if (_interfaceFlags.TryGetValue(id, out byte known)) return known;

        if (Guid.TryParse(id, out Guid guid) && ConvertInterfaceGuidToLuid(guid, out ulong luid) == 0)
        {
            nint row = Marshal.AllocHGlobal(InterfaceRowBytes);

            try
            {
                Marshal.Copy(new byte[InterfaceRowBytes], 0, row, InterfaceRowBytes);
                Marshal.WriteInt64(row, unchecked((long)luid));
                if (GetIfEntry2(row) == 0) return _interfaceFlags[id] = Marshal.ReadByte(row, InterfaceFlagsOffset);
            }
            finally { Marshal.FreeHGlobal(row); }
        }

        return 0;
    }

    public void Dispose()
    {
        _processes.Dispose();
        _performance.Dispose();
    }
}

