using System.Globalization;
using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal sealed record CounterSample(double? Cpu, double? Read, double? Write,
    IReadOnlyList<CoreSample>? Cores, IReadOnlyList<DiskSample>? Disks)
{
    public GpuSnapshot? Gpu { get; init; }
    public static readonly CounterSample Empty = new(null, null, null, null, null);
}

internal sealed class PerformanceCounters : IDisposable
{
    private const uint MoreData = 0x800007D2, FormatDouble = 0x200;
    private const string Total = "_Total";

    [StructLayout(LayoutKind.Sequential)]
    private struct Value { public uint Status; public double Number; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Item { public nint Name; public Value Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, nuint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint user, out nint counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, nint type, out Value value);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bytes, out uint count, nint items);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);

    private nint _query, _cpu, _read, _write, _cores, _diskRead, _diskWrite, _diskIdle, _diskResponse;
    private readonly Dictionary<ulong, string> _gpuNames = GpuCounters.AdapterNames();
    private nint _gpuEngine, _gpuDedicated, _gpuShared;
    private nint _items;
    private uint _capacity;

    public CounterSample Sample()
    {
        if (_query == 0)
        {
            if (PdhOpenQueryW(null, 0, out _query) != 0) return CounterSample.Empty;
            _gpuEngine = Add(@"\GPU Engine(*)\Utilization Percentage");
            _gpuDedicated = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
            _gpuShared = Add(@"\GPU Adapter Memory(*)\Shared Usage");
            _cpu = Add(@"\Processor Information(_Total)\% Processor Time");
            _read = Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            _write = Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            _cores = Add(@"\Processor Information(*)\% Processor Time");
            _diskRead = Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
            _diskWrite = Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
            _diskIdle = Add(@"\PhysicalDisk(*)\% Idle Time");
            _diskResponse = Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        }
        if (PdhCollectQueryData(_query) != 0) return CounterSample.Empty;
        return new(GetValue(_cpu), GetValue(_read), GetValue(_write), Cores(), Disks())
        { Gpu = GpuCounters.Read(GetValues(_gpuEngine), GetValues(_gpuDedicated), GetValues(_gpuShared), _gpuNames) };
    }

    private nint Add(string path) => PdhAddEnglishCounterW(_query, path, 0, out nint counter) == 0 ? counter : 0;

    private static bool Valid(Value value) => value.Status <= 1 && double.IsFinite(value.Number) && value.Number >= 0;

    private static double? GetValue(nint counter) =>
        counter != 0 && PdhGetFormattedCounterValue(counter, FormatDouble, 0, out var value) == 0 && Valid(value)
            ? value.Number : null;

    // A null inventory means the query failed. A named instance may still be warming up.
    private Dictionary<string, double?>? GetValues(nint counter)
    {
        if (counter == 0) return null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint bytes = _capacity;
            uint status = PdhGetFormattedCounterArrayW(counter, FormatDouble, ref bytes, out uint count, _items);
            if (status == MoreData)
            {
                // PDH only guarantees the required byte count on a zero-length sizing call.
                if (_capacity != 0)
                {
                    bytes = 0;
                    if (PdhGetFormattedCounterArrayW(counter, FormatDouble, ref bytes, out _, 0) != MoreData)
                        continue;
                }
                uint capacity = checked(bytes + 4096);
                nint buffer = Marshal.AllocHGlobal((nint)capacity);
                Marshal.FreeHGlobal(_items);
                _items = buffer;
                _capacity = capacity;
                continue;
            }
            if (status != 0) return null;
            var result = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
            int size = Marshal.SizeOf<Item>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Item>(_items + i * size);
                if (Marshal.PtrToStringUni(item.Name) is { Length: > 0 } name)
                    result[name] = Valid(item.Value) ? item.Value.Number : null;
            }
            return result;
        }
        return null;
    }

    private List<CoreSample>? Cores()
    {
        var values = GetValues(_cores);
        if (values is null) return null;
        var cores = new List<CoreSample>();
        foreach (var (name, usage) in values)
        {
            // Instances are "group,number". "_Total" and "group,_Total" are aggregates.
            var parts = name.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int group) &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                cores.Add(new(group, number, usage is { } n ? Math.Clamp(n, 0, 100) : null));
        }
        cores.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Number.CompareTo(b.Number));
        return cores;
    }

    private List<DiskSample>? Disks()
    {
        var read = GetValues(_diskRead);
        var write = GetValues(_diskWrite);
        var idle = GetValues(_diskIdle);
        var response = GetValues(_diskResponse);
        var available = new[] { read, write, idle, response }.OfType<Dictionary<string, double?>>().ToArray();
        if (available.Length == 0) return null;
        var disks = new List<DiskSample>();
        foreach (var name in available.SelectMany(counter => counter.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (name.Equals(Total, StringComparison.OrdinalIgnoreCase)) continue;
            // Instances are the disk number followed by its volumes, such as "0 C: D:".
            int space = name.IndexOf(' ');
            string head = space < 0 ? name : name[..space];
            int? index = int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
            string volumes = index is null || space < 0 ? "" : name[(space + 1)..].Trim();
            disks.Add(new(name, index, volumes, Find(read, name), Find(write, name),
                Find(idle, name) is { } free ? Math.Clamp(100 - free, 0, 100) : null, Find(response, name)));
        }
        disks.Sort((a, b) => a.Index != b.Index
            ? (a.Index ?? int.MaxValue).CompareTo(b.Index ?? int.MaxValue)
            : StringComparer.OrdinalIgnoreCase.Compare(a.Instance, b.Instance));
        return disks;
    }

    private static double? Find(Dictionary<string, double?>? values, string name) =>
        values?.GetValueOrDefault(name);

    public void Dispose()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = 0;
        Marshal.FreeHGlobal(_items);
        _items = 0;
        _capacity = 0;
    }
}
