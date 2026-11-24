using System.Diagnostics;
using Pulse.Telemetry;

namespace Pulse;

public sealed partial class MainWindow
{
    internal async Task RunExtendedAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        for (int i = 0; i < 100 && _frame is null; i++) await Task.Delay(100);
        Require(_frame != null, "No samples.");
        switch (Environment.GetEnvironmentVariable("PULSE_EXTENDED_PHASE"))
        {
            case "profile": await ProfileHistoryAsync(directory); break;
            case "recordings": await VerifyRecordingsAsync(directory); break;
            case "volume": await VerifyVolumeCancellationAsync(directory); break;
            default: throw new ArgumentException("Unknown extended test phase.");
        }
    }

    private async Task ProfileHistoryAsync(string directory)
    {
        string metrics = Path.Combine(directory, "metrics.csv");
        using var self = Process.GetCurrentProcess();
        await File.WriteAllTextAsync(metrics, "build,interval_s,elapsed_s,cpu_ms,working_set_bytes,private_bytes,processes\n");
        foreach (int interval in new[] { 2, 5, 1 })
        {
            IntervalBox.SelectedIndex = interval switch { 1 => 0, 5 => 2, _ => 1 };
            await Task.Delay(6000);
            self.Refresh();
            var cpu = self.TotalProcessorTime;
            var elapsed = Stopwatch.StartNew();
            for (int i = 0; i < (interval == 1 ? 91 : 3); i++)
            {
                await Task.Delay(10000);
                self.Refresh();
                await File.AppendAllTextAsync(metrics, FormattableString.Invariant($"current,{interval},{elapsed.Elapsed.TotalSeconds:F2},{(self.TotalProcessorTime - cpu).TotalMilliseconds:F1},{self.WorkingSet64},{self.PrivateMemorySize64},{_frame!.Tasks.Count}\n"));
            }
        }

        var spans = _history.Samples;
        Require(spans.Count > 100 && spans[^1].Time - spans[0].Time > 890, "The live history did not fill its 15-minute window.");
        await File.WriteAllTextAsync(Path.Combine(directory, "profile.txt"), "PASS 15-minute live history");
    }

    private async Task VerifyRecordingsAsync(string directory)
    {
        IntervalBox.SelectedIndex = 0;
        await Task.Delay(2000);
        var spans = _history.Samples;
        async Task<HistoryCapture> Workload(string file)
        {
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("$w=[Diagnostics.Stopwatch]::StartNew(); while($w.Elapsed.TotalSeconds -lt 20){[void][Math]::Sqrt(121)}");
            double start = spans[^1].Time;
            using var child = Process.Start(info)!;
            await child.WaitForExitAsync();
            await Task.Delay(2000);
            var loaded = Capture();
            int begin = Array.FindIndex(loaded.Samples, s => s.Time >= start);
            var selected = loaded.Slice(begin, loaded.Samples.Length - 1);
            var rank = selected.Rank(0, selected.Samples.Length - 1).Single(c => c.First.Key.Pid == child.Id);
            Require(rank.CpuTicks > TimeSpan.TicksPerSecond && rank.LastIndex < selected.Samples.Length - 1, "Exited workload missing.");
            await selected.SaveAsync(file);
            return selected;
        }

        string a = Path.Combine(directory, "run-a.pulse"), b = Path.Combine(directory, "run-b.pulse");
        var first = await Workload(a);
        var other = await Workload(b);
        await Task.Delay(TimeSpan.FromMinutes(5));
        var saved = await HistoryCapture.OpenAsync(a);
        var fromMemory = Capture();
        int start = Array.FindIndex(fromMemory.Samples, s => s.Time == first.Samples[0].Time);
        int end = Array.FindIndex(fromMemory.Samples, s => s.Time == first.Samples[^1].Time);
        Require(start >= 0 && end >= start, "The workload disappeared from live history.");
        static object[] Values(List<HistoryCapture.Contribution> rows) =>
            rows.Select(c => (object)(c.First.Key, c.CpuTicks, c.PrivateChange, c.Read, c.Write)).ToArray();
        Require(Values(saved.Rank(0, saved.Samples.Length - 1)).SequenceEqual(Values(fromMemory.Rank(start, end))), "Offline rankings differ.");
        var second = await HistoryCapture.OpenAsync(b);
        static (double? Cpu, long? Memory) Peaks(HistoryCapture capture) =>
            (capture.Samples.Max(s => s.CpuPercent), capture.Samples.Max(s => s.UsedMemoryBytes));
        Require(second.Samples.Length > 1 && Peaks(first) == Peaks(saved) && Peaks(other) == Peaks(second), "Offline peaks differ.");
        await File.WriteAllTextAsync(Path.Combine(directory, "recordings.txt"),
            "PASS two workloads, exited process, five-minute retention, offline rankings and peaks.");
    }

    private static async Task VerifyVolumeCancellationAsync(string directory)
    {
        var watch = Stopwatch.StartNew();
        using var cancel = new CancellationTokenSource();
        var drive = new SpaceNode(Path.GetPathRoot(Environment.SystemDirectory)!, null, false);
        var scan = Task.Run(() => SpaceScanner.Scan(drive, cancel.Token));
        await Task.Delay(1500);
        watch.Restart();
        cancel.Cancel();
        try { await scan; } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        watch.Stop();
        Require(watch.ElapsedMilliseconds < 1000, "Cancelling a volume scan took one second or more.");
        await File.WriteAllTextAsync(Path.Combine(directory, "volume.txt"),
            $"PASS system-volume scan cancellation ({watch.ElapsedMilliseconds} ms).");
    }
}
