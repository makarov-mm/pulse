using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal sealed class PerformanceCounters : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Value { public uint Status; public double Number; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, nuint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint user, out nint counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, nint type, out Value value);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);

    private nint _query, _cpu, _read, _write;

    public (double? Cpu, double? Read, double? Write) Sample()
    {
        if (_query == 0)
        {
            if (PdhOpenQueryW(null, 0, out _query) != 0) return (null, null, null);
            PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Time", 0, out _cpu);
            PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", 0, out _read);
            PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", 0, out _write);
        }
        if (PdhCollectQueryData(_query) != 0) return (null, null, null);
        return (GetValue(_cpu), GetValue(_read), GetValue(_write));
    }

    private static double? GetValue(nint counter) =>
        counter != 0 && PdhGetFormattedCounterValue(counter, 0x200, 0, out var value) == 0 &&
        value.Status <= 1 && double.IsFinite(value.Number) && value.Number >= 0 ? value.Number : null;

    public void Dispose()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = 0;
    }
}

