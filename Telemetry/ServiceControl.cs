using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pulse.Telemetry;

// AutomaticDelayed is automatic start with the delayed flag, which Windows keeps apart from the start type.
internal enum ServiceStart { Automatic = 2, Manual = 3, Disabled = 4, AutomaticDelayed = 0x102 }

// Starts, stops and reconfigures a Windows service. Most services require administrator rights for all of these.
internal static class ServiceControl
{
    private const uint Connect = 0x0001, QueryConfig = 0x0001, ChangeConfig = 0x0002, QueryStatus = 0x0004,
        EnumerateDependents = 0x0008, StartAccess = 0x0010, StopAccess = 0x0020, NoChange = 0xFFFFFFFF,
        ControlStop = 1, Stopped = 1, StopPending = 3, Running = 4, ActiveState = 1, DelayedAutoStart = 3;
    private const int MoreData = 234, AlreadyRunning = 1056, NotActive = 1062;
    // ENUM_SERVICE_STATUSW: two string pointers and a SERVICE_STATUS, padded to pointer size.
    [StructLayout(LayoutKind.Sequential)]
    private struct Dependent { public nint Name, DisplayName; public Status Status; }

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
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool EnumDependentServicesW(nint service, uint state, nint buffer, uint bytes, out uint needed, out uint count);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceConfig2W(nint service, uint level, out int info, uint bytes, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfig2W(nint service, uint level, ref int info);
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

    // The running services that depend on this one, directly or through another service, in the order to stop them.
    public static List<string> ActiveDependents(string name) => StopOrder(name, ReadDependents);

    internal static List<string> StopOrder(string name, Func<string, List<string>> readDependents)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var order = new List<string>();
        void Visit(string parent)
        {
            foreach (string child in readDependents(parent))
            {
                if (!visited.Add(child)) continue;
                Visit(child);
                order.Add(child);
            }
        }
        Visit(name);
        return order;
    }

    private static List<string> ReadDependents(string name) => Use(name, EnumerateDependents, service =>
    {
        var names = new List<string>();
        if (EnumDependentServicesW(service, ActiveState, 0, 0, out uint needed, out _)) return names;
        if (Marshal.GetLastWin32Error() != MoreData) throw LastError();
        nint buffer = Marshal.AllocHGlobal((nint)needed);
        try
        {
            if (!EnumDependentServicesW(service, ActiveState, buffer, needed, out _, out uint count)) throw LastError();
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<Dependent>(buffer + i * Marshal.SizeOf<Dependent>());
                if (Marshal.PtrToStringUni(entry.Name) is { Length: > 0 } dependent) names.Add(dependent);
            }
            return names;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    });

    public static void ChangeState(string name, IReadOnlyList<string> confirmed, bool restart)
    {
        var active = ActiveDependents(name);
        if (restart)
        {
            foreach (string service in active.Append(name))
                Use(service, QueryConfig | StartAccess, handle => ReadStartType(handle) != (uint)ServiceStart.Disabled
                    ? true : throw new Win32Exception(1058));
        }
        ApplyStops(name, confirmed, active, restart, StopOne, EnsureRunning);
    }

    // Record accepted stop requests before waiting so partial operations can be recovered.
    internal static void ApplyStops(string name, IReadOnlyList<string> confirmed, IReadOnlyList<string> dependents,
        bool restart, Action<string, Action> stop, Action<string> start)
    {
        if (dependents.Except(confirmed, StringComparer.OrdinalIgnoreCase).Any())
            throw new Win32Exception(1051); // ERROR_DEPENDENT_SERVICES_RUNNING
        var stopped = new Stack<string>();
        Exception? failure = null;
        try
        {
            foreach (string dependent in dependents) stop(dependent, () => stopped.Push(dependent));
            stop(name, () => stopped.Push(name));
            if (!restart) return;
        }
        catch (Exception error) { failure = error; }

        var errors = new List<string>();
        if (failure != null) errors.Add(failure.Message);
        foreach (string service in stopped)
        {
            try { start(service); }
            catch (Exception error) { errors.Add($"{service}: {error.Message}"); }
        }
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors), failure);
    }

    private static Status ReadStatus(nint service) =>
        QueryServiceStatus(service, out var status) ? status : throw LastError();

    private static void WaitForState(nint service, uint state)
    {
        for (int i = 0; i < 75; i++)
        {
            var current = ReadStatus(service);
            if (current.State == state) return;
            if (state == Running && current.State == Stopped)
                throw new Win32Exception(current.ExitCode == 0 ? 1062 : (int)current.ExitCode);
            Thread.Sleep(200);
        }
        throw new TimeoutException(Localization.UiText.Get("ActionTimeout"));
    }

    private static void StopOne(string name, Action accepted) => Use(name, StopAccess | QueryStatus, service =>
    {
        uint state = ReadStatus(service).State;
        if (state == Stopped) return false;
        if (state != Running) throw new Win32Exception(1061); // Preserve paused and transitioning services.
        if (!ControlService(service, ControlStop, out _))
        {
            var error = LastError();
            if (error.NativeErrorCode == NotActive) return false;
            throw error;
        }
        accepted();
        WaitForState(service, Stopped);
        return true;
    });

    private static void EnsureRunning(string name) => Use(name, StartAccess | QueryStatus, service =>
    {
        if (ReadStatus(service).State == StopPending) WaitForState(service, Stopped);
        if (!StartServiceW(service, 0, 0))
        {
            var error = LastError();
            if (error.NativeErrorCode != AlreadyRunning) throw error;
        }
        WaitForState(service, Running);
        return true;
    });

    // Boot and system drivers have start types outside the set offered in the menu.
    public static ServiceStart? StartType(string name) => Use<ServiceStart?>(name, QueryConfig, service =>
    {
        uint start = ReadStartType(service);
        if (start == (uint)ServiceStart.Automatic && IsDelayed(service)) return ServiceStart.AutomaticDelayed;
        return start is >= (uint)ServiceStart.Automatic and <= (uint)ServiceStart.Disabled ? (ServiceStart)start : null;
    });

    private static uint ReadStartType(nint service)
    {
        QueryServiceConfigW(service, 0, 0, out uint needed);
        if (needed == 0) throw LastError();
        nint buffer = Marshal.AllocHGlobal((nint)needed);
        try
        {
            if (!QueryServiceConfigW(service, buffer, needed, out _)) throw LastError();
            return (uint)Marshal.ReadInt32(buffer, 4); // QUERY_SERVICE_CONFIGW.dwStartType
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsDelayed(nint service) =>
        QueryServiceConfig2W(service, DelayedAutoStart, out int delayed, sizeof(int), out _) ? delayed != 0 : throw LastError();

    public static void SetStartType(string name, ServiceStart start) => Use(name, QueryConfig | ChangeConfig, service =>
    {
        bool delayed = start == ServiceStart.AutomaticDelayed;
        uint type = delayed ? (uint)ServiceStart.Automatic : (uint)start;
        uint previous = ReadStartType(service);
        void SetType(uint value)
        {
            if (!ChangeServiceConfigW(service, NoChange, value, NoChange, null, null, 0, null, null, null, null)) throw LastError();
        }

        if (type != previous) SetType(type);
        try
        {
            if (type == (uint)ServiceStart.Automatic && IsDelayed(service) != delayed)
            {
                int flag = delayed ? 1 : 0;
                if (!ChangeServiceConfig2W(service, DelayedAutoStart, ref flag)) throw LastError();
            }
        }
        catch (Win32Exception error) when (type != previous)
        {
            try { SetType(previous); }
            catch (Win32Exception recovery)
            {
                throw new InvalidOperationException($"{error.Message}{Environment.NewLine}{name}: {recovery.Message}", error);
            }
            throw;
        }
        return true;
    });
}
