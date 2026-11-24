using Pulse.Localization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Telemetry;

internal static class ProcessActions
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint rights, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exited,
        out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, StringBuilder text, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessCritical(SafeProcessHandle handle, out bool critical);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle handle, uint code);

    private static SafeProcessHandle OpenVerified(ProcessKey key, uint rights)
    {
        var handle = OpenProcess(0x1000 | rights, false, key.Pid);
        if (handle.IsInvalid || !GetProcessTimes(handle, out long created, out _, out _, out _))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            handle.Dispose();
            throw error;
        }
        if (created == key.Created) return handle;
        handle.Dispose();
        throw new InvalidOperationException(UiText.Get("PidReused"));
    }

    public static string ImagePath(ProcessKey key)
    {
        using var handle = OpenVerified(key, 0);
        int size = 32768;
        var path = new StringBuilder(size);
        if (!QueryFullProcessImageNameW(handle, 0, path, ref size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    public static void End(ProcessKey key)
    {
        if (key.Pid <= 4 || key.Pid == Environment.ProcessId)
            throw new InvalidOperationException(UiText.Get("EndUnavailable"));
        using var handle = OpenVerified(key, 1);
        if (!IsProcessCritical(handle, out bool critical)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (critical) throw new InvalidOperationException(UiText.Get("CriticalProcess"));
        if (!TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}

