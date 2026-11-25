using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pulse.Telemetry;

namespace Pulse;

public sealed partial class MainWindow
{
    internal async Task RunExtendedAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        for (int i = 0; i < 100 && _frame is null; i++) await Task.Delay(100);
        Require(_frame != null, "No samples.");
        await VerifyDiskTrafficAsync(directory);
        await VerifyVolumeCancellationAsync(directory);
    }

    private async Task VerifyDiskTrafficAsync(string directory)
    {
        _timer.Stop();
        while (_busy) await Task.Delay(20);
        await RefreshAsync();
        var baseline = _frame!;
        string path = Path.Combine(directory, $"{Guid.NewGuid()}.bin");
        await Task.Run(() => ExerciseDisk(path));
        await Task.Delay(1000);
        await RefreshAsync();
        var frame = _frame!;
        CheckLiveCards(frame);
        double elapsed = frame.Time - baseline.Time;
        Require(elapsed > 0 && frame.DiskRead * elapsed >= TrafficBytes * 0.90 &&
            frame.DiskWrite * elapsed >= TrafficBytes * 0.90,
            "The disk card did not register a write-through write and an uncached read.");
        await File.WriteAllTextAsync(Path.Combine(directory, "overview.txt"),
            $"PASS live overview. Disk read {frame.DiskRead:F0} B/s, write {frame.DiskWrite:F0} B/s. " +
            $"CPU {frame.Cpu:F1}%, memory {frame.UsedRam}/{frame.TotalRam} bytes, " +
            $"network receive {frame.Received:F0} B/s, send {frame.Sent:F0} B/s.");
        _timer.Start();
    }

    private const int TrafficBytes = 4 * 1024 * 1024;

    private static void ExerciseDisk(string path)
    {
        const int size = TrafficBytes, alignment = 64 * 1024;
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete,
            4096, FileOptions.WriteThrough | FileOptions.DeleteOnClose);
        writer.Write(data);
        writer.Flush(true);
        // Bypass the Windows file cache so the test measures a real storage read.
        using var reader = CreateFileW(path, 0x80000000, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x20000000, 0);
        if (reader.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint buffer = Marshal.AllocHGlobal(size + alignment - 1);
        try
        {
            nint aligned = (buffer + alignment - 1) & ~(nint)(alignment - 1);
            if (!ReadFile(reader, aligned, size, out int bytes, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Require(bytes == size, "Incomplete uncached read.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security,
        FileMode mode, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle file, nint buffer, int size, out int bytes, nint overlapped);

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
