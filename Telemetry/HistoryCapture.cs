using System.Text.Json;

namespace Pulse.Telemetry;

internal readonly record struct ProcessReading(ProcessKey Key, string Name, long CpuTicks, long PrivateBytes,
    long ReadBytes, long WriteBytes, int Threads, uint Handles, double? CpuPercent);

internal sealed record HistorySample(double Time, DateTimeOffset Clock, int IntervalSeconds, double? CpuPercent,
    long? UsedMemoryBytes, ProcessReading[] Processes);

internal sealed record CaptureHost(string Computer, string Processor, int LogicalProcessors, long? PhysicalMemoryBytes);

internal sealed record HistoryCapture(int Version, CaptureHost Host, HistorySample[] Samples)
{
    private const long MaxFileBytes = 256L * 1024 * 1024;

    public static HistoryCapture Create(Frame? frame, IEnumerable<HistorySample> samples) => new(1,
        new(Environment.MachineName, Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "",
            Sampler.LogicalProcessors, frame?.TotalRam), samples.ToArray());

    public HistoryCapture Slice(int start, int end) => this with { Samples = Samples[start..(end + 1)] };

    public static async Task<HistoryCapture> OpenAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException(Pulse.Localization.UiText.Get("RecordingTooLarge"));
        var data = await JsonSerializer.DeserializeAsync<HistoryCapture>(stream);
        if (data is not { Version: 1, Host.LogicalProcessors: > 0, Samples.Length: > 0 and <= 901 })
            throw new InvalidDataException();
        double previous = double.NegativeInfinity;
        foreach (var sample in data.Samples)
        {
            if (sample is null || !double.IsFinite(sample.Time) || sample.Time <= previous ||
                sample.IntervalSeconds is < 1 or > 5 || sample.Processes is null ||
                sample.Processes.Any(p => p.Name is null || p.CpuTicks < 0 || p.ReadBytes < 0 || p.WriteBytes < 0) ||
                sample.Processes.Select(p => p.Key).Distinct().Count() != sample.Processes.Length)
                throw new InvalidDataException();
            previous = sample.Time;
        }
        return data;
    }

    public async Task SaveAsync(string path)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, this);
                if (stream.Length > MaxFileBytes) throw new InvalidDataException(Pulse.Localization.UiText.Get("RecordingTooLarge"));
            }
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }

    internal sealed class Contribution(ProcessReading first, int index)
    {
        public ProcessReading First { get; } = first;
        public ProcessReading Last = first;
        public int FirstIndex { get; } = index;
        public int LastIndex = index;
        public long CpuTicks, Read, Write;
        public int Pairs;
        public long PrivateChange => Last.PrivateBytes - First.PrivateBytes;
    }

    public static bool Gap(HistorySample a, HistorySample b) =>
        b.Time - a.Time > Math.Max(a.IntervalSeconds, b.IntervalSeconds) * 2 + 0.5;

    public List<Contribution> Rank(int start, int end)
    {
        var result = new Dictionary<ProcessKey, Contribution>();
        for (int i = start; i <= end; i++)
        {
            bool continuous = i > start && !Gap(Samples[i - 1], Samples[i]);
            foreach (var p in Samples[i].Processes)
            {
                if (!result.TryGetValue(p.Key, out var c)) { result[p.Key] = new(p, i); continue; }
                if (continuous && c.LastIndex == i - 1)
                {
                    c.CpuTicks += Math.Max(0, p.CpuTicks - c.Last.CpuTicks);
                    c.Read += Math.Max(0, p.ReadBytes - c.Last.ReadBytes);
                    c.Write += Math.Max(0, p.WriteBytes - c.Last.WriteBytes);
                    c.Pairs++;
                }
                c.Last = p;
                c.LastIndex = i;
            }
        }
        return result.Values.OrderByDescending(c => c.CpuTicks).ThenBy(c => c.First.Key.Pid).ToList();
    }
}

internal sealed class ProcessHistory
{
    internal readonly List<HistorySample> Samples = [];
    private Dictionary<ProcessKey, string> _names = [];

    public void Add(Frame frame, int interval)
    {
        Samples.RemoveAll(s => frame.Time - s.Time > 900 || s.Time >= frame.Time);
        if (Samples.Count >= 901) Samples.RemoveRange(0, Samples.Count - 900);
        var names = new Dictionary<ProcessKey, string>(frame.Tasks.Count);
        var rows = new ProcessReading[frame.Tasks.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var t = frame.Tasks[i];
            var p = t.Info;
            names[p.Key] = _names.TryGetValue(p.Key, out var old) && old == p.Name ? old : p.Name;
            rows[i] = new(p.Key, names[p.Key], p.CpuTicks, p.PrivateBytes, p.ReadBytes, p.WriteBytes, p.Threads, p.Handles, t.Cpu);
        }
        _names = names;
        Samples.Add(new(frame.Time, frame.Clock, interval, frame.Cpu, frame.UsedRam, rows));
    }
}
