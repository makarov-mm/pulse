using System.Diagnostics;

namespace Pulse.Telemetry;

internal static class FileExplorer
{
    public static void Select(string path)
    {
        if (!Path.IsPathFullyQualified(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            throw new FileNotFoundException(null, path);
        }

        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }
}
