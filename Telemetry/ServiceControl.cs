using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

internal enum ServiceStart { Automatic = 2, Manual = 3, Disabled = 4 }

// Starts, stops and reconfigures a Windows service. Most services require administrator rights for all of these.
internal static class ServiceControl
{
    private const uint Connect = 0x0001, QueryConfig = 0x0001, ChangeConfig = 0x0002, QueryStatus = 0x0004,
        StartAccess = 0x0010, StopAccess = 0x0020, NoChange = 0xFFFFFFFF, ControlStop = 1, Stopped = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Status { public uint Type, State, ControlsAccepted, ExitCode, ServiceExitCode, CheckPoint, WaitHint; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenServiceW(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartServiceW(nint service, uint count, nint arguments);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(nint service, uint control, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(nint service, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceConfigW(nint service, nint buffer, uint bytes, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfigW(nint service, uint type, uint start, uint errorControl, string? path,
        string? group, nint tag, string? dependencies, string? account, string? password, string? displayName);

    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());

    private static T Use<T>(string name, uint access, Func<nint, T> action)
    {
        nint manager = OpenSCManagerW(null, null, Connect);
        if (manager == 0) throw LastError();
        try
        {
            nint service = OpenServiceW(manager, name, access);
            if (service == 0) throw LastError();
            try { return action(service); }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public static void Start(string name) =>
        Use(name, StartAccess, service => StartServiceW(service, 0, 0) ? true : throw LastError());

    // Asks the service to stop and waits until it has.
    public static void Stop(string name) => Use(name, StopAccess | QueryStatus, service =>
    {
        if (!ControlService(service, ControlStop, out var status)) throw LastError();
        for (int i = 0; i < 75 && status.State != Stopped; i++)
        {
            Thread.Sleep(200);
            if (!QueryServiceStatus(service, out status)) throw LastError();
        }
        return status.State == Stopped ? true : throw new TimeoutException();
    });

    public static void Restart(string name)
    {
        Stop(name);
        Start(name);
    }

    // Null for start types outside the three a user can choose, such as boot and system drivers.
    public static ServiceStart? StartType(string name) => Use(name, QueryConfig, service =>
    {
        QueryServiceConfigW(service, 0, 0, out uint needed);
        if (needed == 0) throw LastError();
        nint buffer = Marshal.AllocHGlobal((nint)needed);
        try
        {
            if (!QueryServiceConfigW(service, buffer, needed, out _)) throw LastError();
            // QUERY_SERVICE_CONFIGW starts with dwServiceType and dwStartType.
            int start = Marshal.ReadInt32(buffer, 4);
            return Enum.IsDefined((ServiceStart)start) ? (ServiceStart?)start : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    });

    public static void SetStartType(string name, ServiceStart start) => Use(name, ChangeConfig, service =>
        ChangeServiceConfigW(service, NoChange, (uint)start, NoChange, null, null, 0, null, null, null, null)
            ? true : throw LastError());
}
