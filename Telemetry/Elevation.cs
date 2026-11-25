using System.Diagnostics;
using System.Security.Principal;

namespace Pulse.Telemetry;

internal static class Elevation
{
    public static bool IsElevated { get; } = Check();

    private static bool Check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void StartElevated()
    {
        string path = Environment.ProcessPath ?? throw new InvalidOperationException();

        using var child = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "runas"
        }) ?? throw new InvalidOperationException(Localization.UiText.Get("ActionError"));
    }
}
