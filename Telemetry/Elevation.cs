using System.Diagnostics;
using System.Security.Principal;

namespace Pulse.Telemetry;

internal static class Elevation
{
    // Whether this process holds administrator rights, which machine-wide startup entries and services require.
    public static bool IsElevated { get; } = Check();

    private static bool Check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Starts a second, elevated copy. Windows asks for consent, and declining throws Win32Exception.
    public static void StartElevated()
    {
        string path = Environment.ProcessPath ?? throw new InvalidOperationException();
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" })?.Dispose();
    }
}
