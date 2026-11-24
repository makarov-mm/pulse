using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Pulse.Telemetry;

internal sealed record GpuDevice(string Id, string Name, double? Dedicated, double? Shared, Dictionary<string, double?> Engines);
internal sealed record GpuLoad(string Adapter, string Engine, double Percent);
internal sealed record GpuSnapshot(IReadOnlyList<GpuDevice> Devices, Dictionary<int, GpuLoad> Processes);

internal static partial class GpuCounters
{
    [GeneratedRegex(@"^(?:pid_(?<pid>\d+)_)?luid_(?<high>0x[\da-f]+)_(?<low>0x[\da-f]+)_phys_(?<phys>\d+)(?:_eng_(?<eng>\d+)_engtype_(?<type>.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Instance();

    public static GpuSnapshot? Read(Dictionary<string, double?>? engines, Dictionary<string, double?>? dedicated,
        Dictionary<string, double?>? shared, IReadOnlyDictionary<ulong, string> names)
    {
        if (engines is null && dedicated is null && shared is null) return null;
        var devices = new Dictionary<string, GpuDevice>();
        var processes = new Dictionary<int, GpuLoad>();
        GpuDevice Device(Match m)
        {
            string id = $"{m.Groups["high"]}_{m.Groups["low"]}_{m.Groups["phys"]}";
            if (devices.TryGetValue(id, out var found)) return found;
            ulong luid = ((ulong)Convert.ToUInt32(m.Groups["high"].Value, 16) << 32) | Convert.ToUInt32(m.Groups["low"].Value, 16);
            string name = names.TryGetValue(luid, out var label) ? label : $"GPU {m.Groups["low"]}";
            if (m.Groups["phys"].Value != "0") name += $" ({m.Groups["phys"]})";
            return devices[id] = new(id, name, null, null, []);
        }
        foreach (var (set, local) in new[] { (dedicated, true), (shared, false) })
        {
            if (set is null) continue;
            foreach (var (key, value) in set)
                if (Instance().Match(key) is { Success: true } m)
                {
                    var d = Device(m);
                    devices[d.Id] = local ? d with { Dedicated = value } : d with { Shared = value };
                }
        }
        if (engines is not null)
            foreach (var (key, value) in engines)
            {
                var m = Instance().Match(key);
                if (!m.Success || !int.TryParse(m.Groups["pid"].Value, out int pid)) continue;
                var d = Device(m);
                string engine = $"{m.Groups["type"]} #{m.Groups["eng"]}";
                d.Engines.TryAdd(engine, null);
                if (value is not { } n) continue;
                d.Engines[engine] = Math.Min(100, (d.Engines[engine] ?? 0) + n);
                if (!processes.TryGetValue(pid, out var old) || n > old.Percent)
                    processes[pid] = new(d.Name, engine, Math.Clamp(n, 0, 100));
            }
        return new(devices.Values.OrderBy(d => d.Id).ToList(), processes);
    }

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapter(nint factory, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Describe(nint adapter, out Description description);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint Vendor, Device, Subsystem, Revision;
        public nuint DedicatedVideo, DedicatedSystem, Shared;
        public ulong Luid;
        public uint Flags;
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    public static Dictionary<ulong, string> AdapterNames()
    {
        var names = new Dictionary<ulong, string>();
        if (CreateDXGIFactory1(new Guid("770aae78-f26f-4dba-a829-253c83d1b387"), out var factory) < 0) return names;
        try
        {
            var enumerate = Method<EnumAdapter>(factory, 12);
            for (uint i = 0; enumerate(factory, i, out var adapter) >= 0; i++)
            {
                try { if (Method<Describe>(adapter, 10)(adapter, out var d) >= 0) names[d.Luid] = d.Name; }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return names;
    }
}
