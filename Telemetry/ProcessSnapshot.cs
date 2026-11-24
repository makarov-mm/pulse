using Pulse.Localization;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal readonly record struct ProcessKey(int Pid, long Created);

internal sealed record ProcessCounters(ProcessKey Key, string Name, int ParentId, int Threads,
    uint Handles, uint Session, long WorkingSet, long PrivateBytes, long CpuTicks, long ReadBytes, long WriteBytes, int BasePriority);

// Created tells a thread apart from a later one that reuses its identifier.
internal sealed record ThreadCounters(int Id, long Created, long CpuTicks);

internal sealed class ProcessSnapshot : IDisposable
{
    // Offsets in the fixed prefix of the x64 SYSTEM_PROCESS_INFORMATION record.
    // The buffer also contains variable-length thread records and Unicode names.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Entry
    {
        [FieldOffset(0)] public uint Next;
        [FieldOffset(4)] public int Threads;
        [FieldOffset(32)] public long Created;
        [FieldOffset(40)] public long User;
        [FieldOffset(48)] public long Kernel;
        [FieldOffset(56)] public ushort NameBytes;
        [FieldOffset(64)] public nint Name;
        [FieldOffset(72)] public int BasePriority;
        [FieldOffset(80)] public nint Pid;
        [FieldOffset(88)] public nint ParentId;
        [FieldOffset(96)] public uint Handles;
        [FieldOffset(100)] public uint Session;
        [FieldOffset(144)] public long WorkingSet;
        [FieldOffset(200)] public long PrivateBytes;
        [FieldOffset(232)] public long ReadBytes;
        [FieldOffset(240)] public long WriteBytes;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, nint buffer, int capacity, out int bytes);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    private int _capacity = 1024 * 1024;
    private nint _buffer;

    // Each process record is followed by one x64 SYSTEM_THREAD_INFORMATION record per thread.
    private const int ProcessBytes = 256, ThreadBytes = 80;

    // Reads every process. The threads of inspectPid, if given, are returned in threads.
    public List<ProcessCounters> Read(int? inspectPid, out List<ThreadCounters> threads)
    {
        threads = [];
        int length;
        while (true)
        {
            if (_buffer == 0) _buffer = Marshal.AllocHGlobal(_capacity);
            int status = NtQuerySystemInformation(5, _buffer, _capacity, out length);
            if (status == 0) break;
            if (status != unchecked((int)0xC0000004))
                throw new Win32Exception((int)RtlNtStatusToDosError(status));
            _capacity = checked(Math.Max(length + 65536, _capacity * 2));
            Dispose();
        }

        var result = new List<ProcessCounters>();
        for (int offset = 0; offset <= length - 256;)
        {
            var entry = Marshal.PtrToStructure<Entry>(_buffer + offset);
            // PID 0 is idle time, not a task consuming CPU.
            if (entry.Pid != 0)
            {
                long nameOffset = entry.Name - _buffer;
                string? name = entry.NameBytes > 0 && nameOffset >= 0 && nameOffset <= length - entry.NameBytes
                    ? Marshal.PtrToStringUni(entry.Name, entry.NameBytes / 2) : null;
                result.Add(new(new((int)entry.Pid, entry.Created), name ?? $"PID {entry.Pid}",
                    (int)entry.ParentId, entry.Threads, entry.Handles, entry.Session, entry.WorkingSet,
                    entry.PrivateBytes, entry.Kernel + entry.User, entry.ReadBytes, entry.WriteBytes, entry.BasePriority));
            }
            if (inspectPid is { } inspected && entry.Pid == inspected)
            {
                // The last record has no Next offset, so its extent is the rest of the buffer.
                long extent = entry.Next != 0 ? (long)entry.Next : length - offset;
                for (int i = 0; i < entry.Threads && ProcessBytes + (i + 1L) * ThreadBytes <= extent; i++)
                {
                    nint thread = _buffer + offset + ProcessBytes + i * ThreadBytes;
                    // KernelTime, UserTime and CreateTime lead the record. The thread identifier is the second half of ClientId.
                    threads.Add(new((int)Marshal.ReadIntPtr(thread, 48), Marshal.ReadInt64(thread, 16),
                        Marshal.ReadInt64(thread, 0) + Marshal.ReadInt64(thread, 8)));
                }
            }
            if (entry.Next == 0) break;
            if (entry.Next < 256 || entry.Next > length - offset)
                throw new InvalidDataException(UiText.Get("IncompleteRecord"));
            offset += (int)entry.Next;
        }
        return result;
    }

    public void Dispose()
    {
        Marshal.FreeHGlobal(_buffer);
        _buffer = 0;
    }
}

