using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal sealed class ProcessorTimes
{
    // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION. Kernel includes idle time.
    [StructLayout(LayoutKind.Sequential)]
    private struct Timing
    {
        public long Idle, Kernel, User, Dpc, Interrupt;
        public uint InterruptCount;
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformationEx(int informationClass, in ushort group, uint inputLength,
        [Out] Timing[] timings, uint outputLength, out uint returned);

    private Dictionary<ushort, Timing[]> _previous = [];

    public (List<CoreSample> Cores, double? Total) Read(bool compare, IReadOnlyList<CoreSample>? counters)
    {
        var current = new Dictionary<ushort, Timing[]>();
        var cores = new List<CoreSample>();
        var fallback = counters?.ToDictionary(c => (c.Group, c.Number));
        double idleSum = 0, elapsedSum = 0;
        int measured = 0;

        ushort groups = GetActiveProcessorGroupCount();
        for (ushort group = 0; group < groups; group++)
        {
            int count = checked((int)GetActiveProcessorCount(group));
            if (count == 0) continue;
            var timings = new Timing[count];
            uint size = (uint)(count * Marshal.SizeOf<Timing>());
            bool valid = NtQuerySystemInformationEx(8, in group, sizeof(ushort), timings, size, out uint returned) >= 0
                && returned == size;
            if (valid) current.Add(group, timings);
            _previous.TryGetValue(group, out var previous);

            for (int cpu = 0; cpu < count; cpu++)
            {
                double? usage = null;
                if (compare && valid && previous?.Length == count)
                {
                    long idle = timings[cpu].Idle - previous[cpu].Idle;
                    long kernel = timings[cpu].Kernel - previous[cpu].Kernel;
                    long user = timings[cpu].User - previous[cpu].User;
                    long elapsed = kernel + user;
                    if (idle >= 0 && kernel >= 0 && user >= 0 && elapsed > 0)
                    {
                        idle = Math.Min(idle, elapsed);
                        usage = 100.0 * (elapsed - idle) / elapsed;
                        idleSum += idle;
                        elapsedSum += elapsed;
                        measured++;
                    }
                }
                usage ??= compare ? fallback?.GetValueOrDefault((group, cpu))?.Usage : null;
                cores.Add(new CoreSample(group, cpu, usage));
            }
        }

        _previous = current;
        return (cores, measured == cores.Count && elapsedSum > 0 ? 100 * (1 - idleSum / elapsedSum) : null);
    }
}
