using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal enum ServiceState { Stopped = 1, StartPending, StopPending, Running, ContinuePending, PausePending, Paused }

// Pid is zero unless the service is hosted by a running process.
internal sealed record ServiceInfo(string Name, string DisplayName, ServiceState State, int Pid);

internal static class ServiceSnapshot
{
    private const uint Connect = 0x0001, EnumerateService = 0x0004, Win32Services = 0x30, AllStates = 0x3;
    private const int MoreData = 234, BufferBytes = 256 * 1024;

    // ENUM_SERVICE_STATUS_PROCESSW: two string pointers followed by SERVICE_STATUS_PROCESS.
    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public nint Name, DisplayName;
        public uint Type, State, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumServicesStatusExW(nint manager, int level, uint type, uint state, nint buffer,
        uint bytes, out uint needed, out uint returned, ref uint resume, string? group);

    public static List<ServiceInfo> Read()
    {
        nint manager = OpenSCManagerW(null, null, Connect | EnumerateService);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        // The service control manager accepts at most 256 KB per call and continues from the resume handle.
        nint buffer = Marshal.AllocHGlobal(BufferBytes);
        try
        {
            var services = new List<ServiceInfo>();
            int size = Marshal.SizeOf<Entry>();
            uint resume = 0;
            while (true)
            {
                bool complete = EnumServicesStatusExW(manager, 0, Win32Services, AllStates, buffer, BufferBytes,
                    out _, out uint count, ref resume, null);
                int error = Marshal.GetLastWin32Error();
                if (!complete && error != MoreData) throw new Win32Exception(error);
                for (int i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<Entry>(buffer + i * size);
                    services.Add(new(Marshal.PtrToStringUni(entry.Name) ?? "", Marshal.PtrToStringUni(entry.DisplayName) ?? "",
                        (ServiceState)entry.State, (int)entry.ProcessId));
                }
                if (complete || count == 0) return services;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(manager);
        }
    }
}
