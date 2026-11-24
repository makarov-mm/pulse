using System.Diagnostics;
using System.Security;
using Microsoft.Win32;
using Pulse.Localization;

namespace Pulse.Telemetry;

internal enum StartupSource { UserRun, MachineRun, MachineRun32, UserRunOnce, MachineRunOnce, UserFolder, CommonFolder }

// Location is the registry key that holds the entry, or the path of the file in a Startup folder.
// Entry is the registry value name or the file name, which also identifies the entry under StartupApproved.
internal sealed record StartupItem(string Name, string Command, StartupSource Source, string Location, bool Enabled, string Entry)
{
    public bool InRegistry => Source is not (StartupSource.UserFolder or StartupSource.CommonFolder);
    // Machine-wide entries can only be changed by an administrator.
    public bool MachineWide => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or
        StartupSource.MachineRunOnce or StartupSource.CommonFolder;
    // Windows keeps no enabled state for RunOnce entries.
    public bool CanToggle => Source is not (StartupSource.UserRunOnce or StartupSource.MachineRunOnce);
}

internal static class StartupItems
{
    private const string Windows = @"Software\Microsoft\Windows\CurrentVersion\";
    private const string Approved = Windows + @"Explorer\StartupApproved\";

    public static List<StartupItem> Read()
    {
        var items = new List<StartupItem>();
        AddValues(items, StartupSource.UserRun, RegistryHive.CurrentUser, RegistryView.Default, "Run", "Run");
        AddValues(items, StartupSource.MachineRun, RegistryHive.LocalMachine, RegistryView.Registry64, "Run", "Run");
        AddValues(items, StartupSource.MachineRun32, RegistryHive.LocalMachine, RegistryView.Registry32, "Run", "Run32");
        AddValues(items, StartupSource.UserRunOnce, RegistryHive.CurrentUser, RegistryView.Default, "RunOnce", null);
        AddValues(items, StartupSource.MachineRunOnce, RegistryHive.LocalMachine, RegistryView.Registry64, "RunOnce", null);
        AddFiles(items, StartupSource.UserFolder, RegistryHive.CurrentUser, Environment.SpecialFolder.Startup);
        AddFiles(items, StartupSource.CommonFolder, RegistryHive.LocalMachine, Environment.SpecialFolder.CommonStartup);
        return items;
    }

    private static void AddValues(List<StartupItem> items, StartupSource source, RegistryHive hive, RegistryView view,
        string name, string? approval)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(Windows + name);
            if (key is null) return;
            var disabled = Disabled(hive, approval);
            string location = (hive == RegistryHive.CurrentUser ? @"HKEY_CURRENT_USER\" : @"HKEY_LOCAL_MACHINE\") +
                (view == RegistryView.Registry32 ? @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\" : Windows) + name;
            foreach (string value in key.GetValueNames())
            {
                if (value.Length == 0) continue;
                string command = key.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                items.Add(new(value, command, source, location, !disabled.Contains(value), value));
            }
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException) { }
    }

    private static void AddFiles(List<StartupItem> items, StartupSource source, RegistryHive hive, Environment.SpecialFolder folder)
    {
        try
        {
            string path = Environment.GetFolderPath(folder);
            if (path.Length == 0 || !Directory.Exists(path)) return;
            var disabled = Disabled(hive, "StartupFolder");
            foreach (string file in Directory.EnumerateFiles(path))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new(Path.GetFileNameWithoutExtension(file), file, source, file, !disabled.Contains(name), name));
            }
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException) { }
    }

    // Windows records entries switched off in Task Manager or Settings under StartupApproved.
    // The first byte of the value is odd for a disabled entry.
    private static HashSet<string> Disabled(RegistryHive hive, string? approval)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (approval is null) return names;
        using var root = RegistryKey.OpenBaseKey(hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);
        using var key = root.OpenSubKey(Approved + approval);
        if (key is null) return names;
        foreach (string value in key.GetValueNames())
            if (key.GetValue(value) is byte[] { Length: > 0 } state && (state[0] & 1) != 0) names.Add(value);
        return names;
    }

    private static RegistryKey Root(StartupItem item) => item.MachineWide
        ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
            item.Source == StartupSource.MachineRun32 ? RegistryView.Registry32 : RegistryView.Registry64)
        : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

    private static string RunKey(StartupItem item) =>
        Windows + (item.Source is StartupSource.UserRunOnce or StartupSource.MachineRunOnce ? "RunOnce" : "Run");

    private static string ApprovalKey(StartupItem item) => Approved + item.Source switch
    {
        StartupSource.MachineRun32 => "Run32",
        StartupSource.UserFolder or StartupSource.CommonFolder => "StartupFolder",
        _ => "Run"
    };

    // StartupApproved always lives in the 64-bit view, including for 32-bit Run entries.
    private static RegistryKey ApprovalRoot(StartupItem item) => item.MachineWide
        ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
        : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

    // Switches the entry on or off the way Task Manager does, without removing it.
    public static void SetEnabled(StartupItem item, bool enabled)
    {
        if (!item.CanToggle) throw new InvalidOperationException();
        using var root = ApprovalRoot(item);
        using var key = root.CreateSubKey(ApprovalKey(item));
        // Four status bytes, then the time the entry was disabled as a FILETIME.
        var state = new byte[12];
        state[0] = (byte)(enabled ? 2 : 3);
        if (!enabled) BitConverter.TryWriteBytes(state.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());
        key.SetValue(item.Entry, state, RegistryValueKind.Binary);
    }

    // Applies the changes made in the entry editor. A file in a Startup folder only takes the enabled state.
    public static void Save(StartupItem item, string name, string command, bool enabled)
    {
        var saved = item;
        if (item.InRegistry && (name != item.Entry || command != item.Command))
        {
            using var root = Root(item);
            using var key = root.OpenSubKey(RunKey(item), true) ?? throw new IOException();
            // Value names ignore case, so a change of case alone is not a move to another value.
            bool moved = !name.Equals(item.Entry, StringComparison.OrdinalIgnoreCase);
            if (moved && key.GetValue(name) is not null)
                throw new InvalidOperationException(UiText.Format("StartupNameTaken", name));
            var kind = key.GetValue(item.Entry) is not null && key.GetValueKind(item.Entry) == RegistryValueKind.ExpandString
                ? RegistryValueKind.ExpandString : RegistryValueKind.String;
            if (!moved && name != item.Entry) key.DeleteValue(item.Entry, false);
            key.SetValue(name, command, kind);
            if (moved)
            {
                key.DeleteValue(item.Entry, false);
                if (item.CanToggle) MoveApproval(item, name);
            }
            saved = item with { Name = name, Entry = name, Command = command };
        }
        if (saved.CanToggle && enabled != item.Enabled) SetEnabled(saved, enabled);
    }

    // Carries the enabled state over to the new name of a renamed entry.
    private static void MoveApproval(StartupItem item, string name)
    {
        using var root = ApprovalRoot(item);
        using var key = root.OpenSubKey(ApprovalKey(item), true);
        if (key?.GetValue(item.Entry) is not byte[] state) return;
        key.SetValue(name, state, RegistryValueKind.Binary);
        key.DeleteValue(item.Entry, false);
    }

    // Removes the entry and its enabled state. A file in a Startup folder is deleted.
    public static void Delete(StartupItem item)
    {
        if (item.InRegistry)
        {
            using var root = Root(item);
            using var key = root.OpenSubKey(RunKey(item), true) ?? throw new IOException();
            key.DeleteValue(item.Entry, false);
        }
        else File.Delete(item.Location);
        if (!item.CanToggle) return;
        using var approvalRoot = ApprovalRoot(item);
        using var approval = approvalRoot.OpenSubKey(ApprovalKey(item), true);
        approval?.DeleteValue(item.Entry, false);
    }

    // The file name of the program a startup command launches. Empty when it cannot be told from the text.
    public static string Executable(string command)
    {
        string text = Environment.ExpandEnvironmentVariables(command).Trim();
        string path;
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            path = end > 0 ? text[1..end] : text[1..];
        }
        else
        {
            int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe >= 0 ? text[..(exe + 4)] : text.Split(' ')[0];
        }
        try { return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(path) : ""; }
        catch (ArgumentException) { return ""; }
    }

    // Shows the file of a Startup folder entry in File Explorer.
    public static void ShowFile(StartupItem item)
    {
        if (item.InRegistry) throw new InvalidOperationException();
        FileExplorer.Select(item.Location);
    }
}
