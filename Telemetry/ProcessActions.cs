using Pulse.Localization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Telemetry;

// Facts read once when a process is selected. A part Windows withholds is empty or null.
internal sealed record ProcessIdentity(string CommandLine, uint? PriorityClass, string User);

internal static class ProcessActions
{
    private const uint TokenQuery = 0x0008;
    private const int CommandLineClass = 60, CommandLineBytes = 0x10000 + 32;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(SafeProcessHandle handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle handle, uint access, out SafeAccessTokenHandle token);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle handle, int infoClass, nint buffer, int bytes, out int returned);

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

    // Never throws: each part is read independently and left empty when access is denied.
    public static ProcessIdentity Describe(ProcessKey key)
    {
        try
        {
            using var handle = OpenVerified(key, 0);
            uint priority = GetPriorityClass(handle);
            return new(CommandLine(handle), priority == 0 ? (uint?)null : priority, User(handle));
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return new("", null, "");
        }
    }

    private static string CommandLine(SafeProcessHandle handle)
    {
        nint buffer = Marshal.AllocHGlobal(CommandLineBytes);
        try
        {
            if (NtQueryInformationProcess(handle, CommandLineClass, buffer, CommandLineBytes, out _) != 0) return "";
            // The buffer holds a UNICODE_STRING: a byte length, then a pointer to the text that follows it.
            int length = (ushort)Marshal.ReadInt16(buffer);
            nint text = Marshal.ReadIntPtr(buffer, 8);
            return text == 0 || length == 0 ? "" : Marshal.PtrToStringUni(text, length / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string User(SafeProcessHandle handle)
    {
        if (!OpenProcessToken(handle, TokenQuery, out var token)) return "";
        using (token)
        {
            try
            {
                using var identity = new System.Security.Principal.WindowsIdentity(token.DangerousGetHandle());
                return identity.Name;
            }
            catch (Exception error) when (error is ArgumentException or System.Security.SecurityException or
                UnauthorizedAccessException or System.Security.Principal.IdentityNotMappedException)
            {
                return "";
            }
        }
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

