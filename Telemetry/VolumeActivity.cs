using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Telemetry;

// Each volume GUID identifies one set of device counters, even when a volume has several mount paths.
internal sealed class VolumeActivity
{
    [StructLayout(LayoutKind.Sequential, Size = 88)]
    internal struct Counters
    {
        public long BytesRead, BytesWritten, ReadTime, WriteTime, IdleTime;
        public uint ReadCount, WriteCount, QueueDepth, SplitCount;
        public long QueryTime;
        public uint DeviceNumber;
        // The remaining bytes hold StorageManagerName[8] and alignment padding.
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindFirstVolumeW(StringBuilder name, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextVolumeW(nint search, StringBuilder name, uint length);
    [DllImport("kernel32.dll")]
    private static extern bool FindVolumeClose(nint search);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string name, StringBuilder paths, uint length, out uint needed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security,
        FileMode mode, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize,
        out Counters output, uint outputSize, out uint returned, nint overlapped);

    private Dictionary<string, string>? _mounts;
    private double _nextInventory;
    private Dictionary<string, Counters> _previous = [];
    private double? _lastSample;

    internal void Reset()
    {
        _previous.Clear();
        _lastSample = null;
    }

    internal List<DiskSample>? Sample(double time, bool compare)
    {
        if (time >= _nextInventory || _mounts is null)
        {
            _mounts = MountedVolumes();
            _nextInventory = time + 5;
        }
        if (_mounts is null) { Reset(); return null; }

        var current = new Dictionary<string, Counters>();
        var disks = new List<DiskSample>(_mounts.Count);
        double elapsed = time - (_lastSample ?? time);
        foreach (var (device, mount) in _mounts)
        {
            using var handle = CreateFileW(device, 0, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0, 0);
            // IOCTL_DISK_PERFORMANCE returns cumulative storage-driver statistics with a metadata-only handle.
            if (!handle.IsInvalid && DeviceIoControl(handle, 0x00070020, 0, 0, out var now,
                    (uint)Marshal.SizeOf<Counters>(), out uint bytes, 0) && bytes == Marshal.SizeOf<Counters>())
            {
                current.Add(device, now);
                if (compare && elapsed is > 0 and <= Sampler.MaxSampleInterval && _previous.TryGetValue(device, out var old))
                {
                    disks.Add(Measure(device, mount, old, now, elapsed));
                    continue;
                }
            }
            disks.Add(new DiskSample(device, null, mount, null, null, null, null) { IsVolume = true });
        }
        _lastSample = time;
        _previous = current;
        return disks;
    }

    internal static DiskSample Measure(string device, string mount, Counters old, Counters now, double seconds)
    {
        double? read = Sampler.Rate(now.BytesRead, old.BytesRead, seconds);
        double? write = Sampler.Rate(now.BytesWritten, old.BytesWritten, seconds);
        long elapsedTicks = now.QueryTime - old.QueryTime;
        long idle = now.IdleTime - old.IdleTime;
        double? active = elapsedTicks > 0 && idle >= 0 ? Math.Clamp(100.0 * (1 - (double)idle / elapsedTicks), 0, 100) : null;
        long operations = (long)now.ReadCount - old.ReadCount + (long)now.WriteCount - old.WriteCount;
        double? response = operations > 0 && now.ReadCount >= old.ReadCount && now.WriteCount >= old.WriteCount &&
            now.ReadTime >= old.ReadTime && now.WriteTime >= old.WriteTime
            ? ((double)(now.ReadTime - old.ReadTime) + (now.WriteTime - old.WriteTime)) / TimeSpan.TicksPerSecond / operations : null;
        return new DiskSample(device, null, mount, read, write, active, response) { IsVolume = true };
    }

    private static Dictionary<string, string>? MountedVolumes()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var name = new StringBuilder(64); // Volume GUID path, including trailing backslash.
        nint search = FindFirstVolumeW(name, (uint)name.Capacity);
        if (search == -1) return null;
        try
        {
            var paths = new StringBuilder(260);
            do
            {
                string volume = name.ToString();
                if (GetDriveTypeW(volume) is not (2 or 3)) continue; // Removable or fixed local storage.
                if (!GetVolumePathNamesForVolumeNameW(volume, paths, (uint)paths.Capacity, out uint needed))
                {
                    if (Marshal.GetLastWin32Error() != 234 || needed > int.MaxValue) continue;
                    paths.EnsureCapacity((int)needed);
                    if (!GetVolumePathNamesForVolumeNameW(volume, paths, (uint)paths.Capacity, out _)) continue;
                }
                // The multi-string's first path is sufficient as a display name.
                if (paths.Length > 0) result[volume.TrimEnd('\\')] = paths.ToString();
            }
            while (FindNextVolumeW(search, name, (uint)name.Capacity));
            return Marshal.GetLastWin32Error() == 18 ? result : null; // ERROR_NO_MORE_FILES
        }
        finally { FindVolumeClose(search); }
    }
}
